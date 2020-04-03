using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Discord.Net;
using Discord.Rest;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;

namespace SLB
{
    static class Discord
    {
        private static DiscordSocketClient discordSocketClient;
        private static CommandService commandService;
        private static IServiceProvider serviceProvider;
        public static string token;
        public static bool loggedIn;
        // messages, organised by guild and channel
        public static Dictionary<ulong, Tuple<ITextChannel, List<IUserMessage>>> currentStatusMessages;
        public static int lastMessageCount;

        // message clock format
        const string CLOCK_FORMAT = "dd/MM/yy HH:mm";

        static readonly Color LOBBY_COLOUR = Color.Gold;
        const int ALLOCATED_MESSAGES = 10;

        public static void Run()
        {
            discordSocketClient = new DiscordSocketClient(new DiscordSocketConfig {ExclusiveBulkDelete = true});
            commandService = new CommandService();
            serviceProvider = new ServiceCollection()
                .AddSingleton(discordSocketClient)
                .AddSingleton(commandService)
                .BuildServiceProvider();

            currentStatusMessages = new Dictionary<ulong, Tuple<ITextChannel, List<IUserMessage>>>();
            
            if (File.Exists("token.txt"))
            {
                token = File.ReadAllText("token.txt");
                Console.WriteLine("Using persisted bot token.");
            }
            if (string.IsNullOrEmpty(token))
            {
                token = Web.InputRequest("Enter bot token.");
            }

            discordSocketClient.Log += DiscordSocketClient_Log;
            discordSocketClient.LoggedIn += DiscordSocketClient_LoggedIn;
            discordSocketClient.LoggedOut += DiscordSocketClient_LoggedOut;
            RegisterCommandsAsync().GetAwaiter().GetResult();
            LoginAsync().GetAwaiter().GetResult();
        }

        public static async Task UpdateStatus(int playerCount, LobbyCounts lobbyCounts, List<LobbyInfo> lobbyInfos)
        {
            Console.WriteLine("Updating Discord status messages...");
            
            // Message storage
            List<string> messages = new List<string>();
            List<Embed> embeds = new List<Embed>();

            // Create overview message
            if (playerCount >= 0)
            {
                string statusOverview = string.Format("**__S&ASRT lobby status — {0} GMT__**", DateTime.Now.ToString(CLOCK_FORMAT));
                statusOverview += string.Format("\n\n**{0}** people are playing S&ASRT.", playerCount);
                statusOverview += "\n" + LobbyCountMessage(lobbyCounts.matchmakingLobbies, lobbyCounts.matchmakingPlayers, "matchmaking");
                statusOverview += "\n" + LobbyCountMessage(lobbyCounts.customGameLobbies, lobbyCounts.customGamePlayers, "custom game");
                messages.Add(statusOverview);
                embeds.Add(null);
            }
            else
            {
                messages.Add("Bot is not logged into Steam!");
                embeds.Add(null);
            }

            // Create lobby messages
            foreach (var lobbyInfo in lobbyInfos)
            {
                // Skip displaying custom lobbies
                if (lobbyInfo.type == 3)
                {
                    continue;
                }

                EmbedBuilder builder = new EmbedBuilder();
                builder.WithColor(LOBBY_COLOUR);
                builder.WithTitle(lobbyInfo.name);
                builder.AddField("Players", lobbyInfo.playerCount + "/10", true);
                if (lobbyInfo.type >= 0)
                {
                    builder.AddField("Type", LobbyTools.GetLobbyType(lobbyInfo.type), true);
                    if (lobbyInfo.type != 3)
                    {
                        builder.WithDescription(string.Format("steam://joinlobby/{0}/{1}", Steam.APPID, lobbyInfo.id));
                    }
                    else
                    {
                        builder.WithDescription("Private lobby");
                    }
                }
                if (lobbyInfo.state >= 0)
                {
                    builder.AddField("Activity", LobbyTools.GetActivity(lobbyInfo.state, lobbyInfo.raceProgress, lobbyInfo.countdown), true);
                    builder.AddField("Event", LobbyTools.GetEvent(lobbyInfo.type, lobbyInfo.matchMode), true);
                    string[] map = LobbyTools.GetMap(lobbyInfo.type, lobbyInfo.matchMode);
                    builder.AddField(map[0], map[1], true);
                    builder.AddField("Difficulty", LobbyTools.GetDifficulty(lobbyInfo.type, lobbyInfo.difficulty),true);
                }
                else
                {
                    builder.WithDescription("Lobby initialising...");
                }
                messages.Add("");
                embeds.Add(builder.Build());
            }

            var guilds = await discordSocketClient.Rest.GetGuildsAsync();
            foreach(var guild in guilds)
            {
                // Set up the status channel in each guild
                bool newChannel = !currentStatusMessages.TryGetValue(guild.Id, out var channelMessagePair);
                if (newChannel)
                {
                    Console.WriteLine("Setting up status channel on {0} ({1})...", guild.Name, guild.Id);
                    try{    
                        // Look for old status channels
                        var textChannels = await guild.GetTextChannelsAsync();
                        RestTextChannel statusChannel = null;
                        foreach (var channel in textChannels)
                        {
                            if (channel.Name.EndsWith("-in-matchmaking"))
                            {
                                statusChannel = channel;
                                break;
                            }
                        }

                        List<IUserMessage> statusMessages = new List<IUserMessage>();
                         // Reuse old messages if channel exists
                        if (statusChannel != null)
                        {
                            await foreach (var discordMessages in statusChannel.GetMessagesAsync())
                            {
                                foreach (IUserMessage discordMessage in discordMessages)
                                { 
                                    if (discordMessage.Author.Id == discordSocketClient.CurrentUser.Id)
                                    {
                                        statusMessages.Add(discordMessage);
                                    }
                                }
                            }
                            // Ensure the status messages are ordered correctly
                            statusMessages.Sort((x, y) => x.Timestamp.CompareTo(y.Timestamp));

                            // Sufficient messages, we assume that these messages were combined previously.
                            if (statusMessages.Count > ALLOCATED_MESSAGES)
                            {
                                // Delete excess
                                await statusChannel.DeleteMessagesAsync(statusMessages.GetRange(ALLOCATED_MESSAGES, statusMessages.Count - ALLOCATED_MESSAGES));
                                statusMessages.RemoveRange(ALLOCATED_MESSAGES, statusMessages.Count - ALLOCATED_MESSAGES);
                            }
                            // Insufficient messages, start from scratch to ensure the messages are combined.
                            else if (statusMessages.Count < ALLOCATED_MESSAGES)
                            {
                                await statusChannel.DeleteMessagesAsync(statusMessages);
                                statusMessages.Clear();
                            }
                        }
                        // Create status channel if not found
                        else
                        {
                            statusChannel = await guild.CreateTextChannelAsync("xx-in-matchmaking");
                        }
                        // Store channel/message pair
                        channelMessagePair = new Tuple<ITextChannel, List<IUserMessage>>(statusChannel, statusMessages);
                        currentStatusMessages.Add(guild.Id, channelMessagePair);
                        Console.WriteLine("Status channel setup complete!");
                    }
                    catch(HttpException e)
                    {
                        Console.WriteLine("Status channel setup failed!");
                        UpdateStatusError(guild.Id, e);
                        continue;
                    }
                }
                
                // Set channel name
                try
                {
                    await channelMessagePair.Item1.ModifyAsync(c => {c.Name = (lobbyCounts.matchmakingPlayers >= 0 ? lobbyCounts.matchmakingPlayers.ToString() : "xx") + "-in-matchmaking";});
                }
                catch(HttpException e)
                {
                    Console.WriteLine("Failed to set status channel name on server {0} ({1})", guild.Name, guild.Id);
                    UpdateStatusError(guild.Id, e);
                    continue;
                }

                // Send/update messages
                // Case true: Ensures a new hannel has messages allocated.
                // Case false: Update/send the appropriate subset of messages.
                int count = newChannel ? ALLOCATED_MESSAGES : Math.Max(messages.Count, lastMessageCount);
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        string message = i < messages.Count ? messages[i] : "** **";
                        Embed embed = i < embeds.Count ? embeds[i] : null;

                        if (i < channelMessagePair.Item2.Count)
                        {
                            await channelMessagePair.Item2[i].ModifyAsync(m => {m.Content = message; m.Embed = embed;});
                        }
                        else
                        {
                            channelMessagePair.Item2.Add(await channelMessagePair.Item1.SendMessageAsync(message, embed: embed));
                        }
                    }
                }
                catch (HttpException e)
                {
                    Console.WriteLine("Failed to send/update a message to server {0} ({1})", guild.Name, guild.Id);                  
                    UpdateStatusError(guild.Id, e);
                    continue;
                }                
            }
            lastMessageCount = lobbyInfos.Count + 1;
            
            Console.WriteLine("Updated Discord status messages!");
        }

        public static void UpdateStatusError(ulong guildId, HttpException e)
        {
            switch (e.DiscordCode)
            {
                case 10003:
                    Console.WriteLine("Channel cound not be found!");
                    currentStatusMessages.Remove(guildId);
                    return;
                case 50001:
                    Console.WriteLine("Bot does not have permission!");
                    currentStatusMessages.Remove(guildId);
                    return;
                default:
                    Console.WriteLine(e);
                    return;
            }
        }

        public static string LobbyCountMessage(int lobbyCount, int playerCount, string lobbyType)
        {
            if (playerCount == 0)
            {
                return string.Format("**There are no {0} lobbies!**", lobbyType);
            }
            else if (playerCount == 1)
            {
                return string.Format("**1** player is in a {0} lobby.", lobbyType);
            }
            else
            {
                return string.Format("**{0}** players are in **{1}** {2} {3}.", playerCount, lobbyCount, lobbyType, lobbyCount > 1 ? "lobbies" : "lobby");;
            }
        }

        public static async Task LoginAsync()
        {
            Console.WriteLine("Logging into Discord...");
            try
            {      
                await discordSocketClient.LoginAsync(TokenType.Bot, token);
                await discordSocketClient.StartAsync();
            }
            catch (Exception e)
            {
                Console.WriteLine("Failed to log into Discord!");
                Console.WriteLine(e);
            }
        }

        private static Task DiscordSocketClient_Log(LogMessage arg)
        {
            Console.WriteLine(arg);
            return Task.CompletedTask;
        }

        private static Task DiscordSocketClient_LoggedIn()
        {
            Console.WriteLine("Logged into Discord!");
            loggedIn = true;

            Console.WriteLine("Saving token file...");
            File.WriteAllText("token.txt", token);
            Console.WriteLine("Saved token file!");

            return Task.CompletedTask;
        }

        private static async Task DiscordSocketClient_LoggedOut()
        {
            Console.WriteLine("Logged out of Discord, logging back on in 5...");
            loggedIn = false;
            await Task.Delay(5000);
            await LoginAsync();
        }

        public static async Task RegisterCommandsAsync()
        {
            discordSocketClient.MessageReceived += HandleCommandAsync;
            await commandService.AddModulesAsync(Assembly.GetEntryAssembly(), serviceProvider);
        }

        private static async Task HandleCommandAsync(SocketMessage arg)
        {
            var message = arg as SocketUserMessage;
            var context = new SocketCommandContext(discordSocketClient, message);
            if (message.Author.IsBot) return;

            int argPos = 0;
            if (message.HasStringPrefix("!", ref argPos))
            {
                var result = await commandService.ExecuteAsync(context, argPos, serviceProvider);
                if (!result.IsSuccess) Console.WriteLine(result.ErrorReason);
            }
        }
    }
}
