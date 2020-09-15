using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Valkyrja.entities;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using guid = System.UInt64;

namespace Valkyrja.coreLite
{
	public partial class ValkyrjaClient<T>: IValkyrjaClient, IDisposable where T: BaseConfig, new()
	{
		public BaseConfig CoreConfig{ get; set; }
		public T Config{ get; set; }
		public Monitoring Monitoring{ get; set; }
		public DiscordSocketClient DiscordClient;
		public Events Events;
		private int CurrentShardId = 0;

		public DateTime TimeStarted{ get; private set; }
		private DateTime TimeConnected = DateTime.MaxValue;
		private bool IsInitialized = false;

		public bool IsConnected{
			get => this.DiscordClient.LoginState == LoginState.LoggedIn &&
			       this.DiscordClient.ConnectionState == ConnectionState.Connected &&
			       this._Connected;
			set => this._Connected = value;
		}

		private bool _Connected = false;

		private CancellationTokenSource MainUpdateCancel;
		private Task MainUpdateTask = null;
		private readonly SemaphoreSlim GuildAvailableLock = new SemaphoreSlim(1, 1);

		public readonly List<IModule> Modules = new List<IModule>();

		private const string GameStatusConnecting = "Connecting...";
		private readonly Regex RegexCommandParams = new Regex("\"[^\"]+\"|\\S+", RegexOptions.Compiled);
		private readonly Regex RegexEveryone = new Regex("(@everyone)|(@here)", RegexOptions.Compiled);
		private readonly Regex RegexCustomCommandPmAll = new Regex("^<pm(-sender)?>", RegexOptions.Compiled);
		private readonly Regex RegexCustomCommandPmMentioned = new Regex("^<pm>", RegexOptions.Compiled);
		private readonly Regex RngRegex = new Regex("(?<=<\\|>).*?(?=<\\|>)", RegexOptions.Singleline | RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

		public ConcurrentDictionary<guid, Server> Servers{ get; set; } = new ConcurrentDictionary<guid, Server>();
		public readonly Dictionary<string, Command> Commands = new Dictionary<string, Command>();
		private Dictionary<guid, int> FailedPmCount = new Dictionary<guid, int>();
		public List<Operation> CurrentOperations{ get; set; } = new List<Operation>();
		public Object OperationsLock{ get; set; } = new Object();

		public int OperationsRan{ get; set; } = 0;


		public ValkyrjaClient(int shardIdOverride = -1, string configPath = null)
		{
			this.TimeStarted = DateTime.UtcNow;

			Console.WriteLine("ValkyrjaClient: Loading configuration...");
			this.CoreConfig = this.Config = Valkyrja.entities.BaseConfig.Load<T>(configPath);
			if( string.IsNullOrEmpty(this.CoreConfig.DiscordToken) )
			{
				Console.WriteLine("ValkyrjaClient: Discord token is empty. Exiting.");
				Environment.Exit(0);
			}

			this.Monitoring = Monitoring.Create(this.CoreConfig, shardIdOverride);
			if( shardIdOverride >= 0 )
				this.CurrentShardId = shardIdOverride;
		}

		public void Dispose()
		{
			Console.WriteLine("ValkyrjaClient: Disposing.");
			this.Monitoring?.Dispose();

			Console.WriteLine("ValkyrjaClient: Disposed.");
		}

		public async Task Connect()
		{
			Console.WriteLine($"ValkyrjaClient: Shard {this.CurrentShardId} taken.");

			DiscordSocketConfig config = new DiscordSocketConfig{
				ShardId = this.CurrentShardId,
				TotalShards = this.CoreConfig.TotalShards,
				LogLevel = this.CoreConfig.Debug ? LogSeverity.Debug : LogSeverity.Warning,
				DefaultRetryMode = RetryMode.Retry502 & RetryMode.RetryRatelimit & RetryMode.RetryTimeouts,
				AlwaysDownloadUsers = this.CoreConfig.DownloadUsers,
				LargeThreshold = 100,
				HandlerTimeout = null,
				MessageCacheSize = this.CoreConfig.MessageCacheSize,
				ConnectionTimeout = 300000
			};

			this.DiscordClient = new DiscordSocketClient(config);

			if( this.CoreConfig.Debug )
			{
				this.DiscordClient.Log += message => {
					Console.WriteLine($"[${message.Severity}] ${message.Message}\n  Source: ${message.Source}");
					return Task.CompletedTask;
				};
			}

			this.DiscordClient.Connecting += OnConnecting;
			this.DiscordClient.Connected += OnConnected;
			this.DiscordClient.Ready += OnReady;
			this.DiscordClient.Disconnected += OnDisconnected;
			this.Events = new Events(this.DiscordClient, this);
			this.Events.MessageReceived += OnMessageReceived;
			this.Events.MessageUpdated += OnMessageUpdated;
			this.Events.Connected += async () => await this.DiscordClient.SetGameAsync(this.CoreConfig.GameStatus);
			this.Events.Initialize += InitCommands;
			this.Events.Initialize += InitModules;
			this.Events.GuildAvailable += OnGuildAvailable;
			this.Events.JoinedGuild += OnGuildJoined;
			this.Events.LeftGuild += OnGuildLeft;
			this.Events.GuildUpdated += OnGuildUpdated;

			await this.DiscordClient.LoginAsync(TokenType.Bot, this.CoreConfig.DiscordToken);
			await this.DiscordClient.StartAsync();
		}

//Events
		private Task OnConnecting()
		{
			Console.WriteLine("ValkyrjaClient: Connecting...");
			return Task.CompletedTask;
		}

		private async Task OnConnected()
		{
			Console.WriteLine("ValkyrjaClient: Connected.");

			try
			{
				this.TimeConnected = DateTime.Now;
				await this.DiscordClient.SetGameAsync(GameStatusConnecting);
			}
			catch( Exception e )
			{
				await LogException(e, "--OnConnected");
			}
		}

		private Task OnReady()
		{
			if( this.CoreConfig.Debug )
				Console.WriteLine("ValkyrjaClient: Ready.");

			if( this.MainUpdateTask == null )
			{
				this.MainUpdateCancel = new CancellationTokenSource();
				this.MainUpdateTask = Task.Factory.StartNew(MainUpdate, this.MainUpdateCancel.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
				this.MainUpdateTask.Start();
			}

			return Task.CompletedTask;
		}

		private async Task OnDisconnected(Exception exception)
		{
			Console.WriteLine("ValkyrjaClient: Disconnected.");
			this.IsConnected = false;
			this.Monitoring?.Disconnects.Inc();
			this.Monitoring?.Disconnects.Publish();

			await LogException(exception, "--D.NET Client Disconnected");

			try
			{
				if( this.Events.Disconnected != null )
					await this.Events.Disconnected(exception);
			}
			catch( Exception e )
			{
				await LogException(e, "--Events.Disconnected");
			}

			if( exception.Message == "Server requested a reconnect" ||
			    exception.Message == "Server missed last heartbeat" )
				return;

			Dispose();
			Console.WriteLine("Shutting down.");
			Environment.Exit(0); //HACK - The library often reconnects in really shitty way and no longer works
		}

// Message events
		private async Task OnMessageReceived(SocketMessage message)
		{
			if( !this.IsConnected )
				return;

			try
			{
				if( this.CoreConfig.Debug )
					Console.WriteLine("ValkyrjaClient: MessageReceived on thread " + Thread.CurrentThread.ManagedThreadId);

				if( !(message.Channel is SocketTextChannel channel) )
				{
					//await LogMessage(LogType.Pm, null, message);
					return;
				}

				Server server;
				if( !this.Servers.ContainsKey(channel.Guild.Id) || (server = this.Servers[channel.Guild.Id]) == null )
					return;
				if( this.CoreConfig.IgnoreBots && message.Author.IsBot || this.CoreConfig.IgnoreEveryone && this.RegexEveryone.IsMatch(message.Content) )
					return;

				bool commandExecuted = false;
				string prefix;
				if( message.Author.Id != this.DiscordClient.CurrentUser.Id &&
				    !string.IsNullOrWhiteSpace(this.CoreConfig.CommandPrefix) && message.Content.StartsWith(prefix = this.CoreConfig.CommandPrefix) )
					commandExecuted = await HandleCommand(server, channel, message, prefix);

				if( !commandExecuted && message.MentionedUsers.Any(u => u.Id == this.DiscordClient.CurrentUser.Id) )
					await HandleMentionResponse(server, channel, message);
			}
			catch( Exception exception )
			{
				await LogException(exception, "--OnMessageReceived");
			}
		}

		private async Task OnMessageUpdated(IMessage originalMessage, SocketMessage updatedMessage, ISocketMessageChannel iChannel)
		{
			if( !this.IsConnected || originalMessage.Content == updatedMessage.Content )
				return;

			try
			{
				Server server;
				if( !(iChannel is SocketTextChannel channel) || updatedMessage?.Author == null || !this.Servers.ContainsKey(channel.Guild.Id) || (server = this.Servers[channel.Guild.Id]) == null || this.CoreConfig == null )
					return;
				if( this.CoreConfig.IgnoreBots && updatedMessage.Author.IsBot || this.CoreConfig.IgnoreEveryone && this.RegexEveryone.IsMatch(updatedMessage.Content) )
					return;

				bool commandExecuted = false;
				if( this.CoreConfig.ExecuteOnEdit )
				{
					string prefix;
					if( updatedMessage.Author.Id != this.DiscordClient.CurrentUser.Id &&
					    !string.IsNullOrWhiteSpace(this.CoreConfig.CommandPrefix) && updatedMessage.Content.StartsWith(prefix = this.CoreConfig.CommandPrefix) )
						commandExecuted = await HandleCommand(server, channel, updatedMessage, prefix);
				}
			}
			catch( Exception exception )
			{
				await LogException(exception, "--OnMessageUpdated");
			}
		}

//Update
		private async Task MainUpdate()
		{
			if( this.CoreConfig.Debug )
				Console.WriteLine("ValkyrjaClient: MainUpdate started.");

			while( !this.MainUpdateCancel.IsCancellationRequested )
			{
				if( this.CoreConfig.Debug )
					Console.WriteLine("ValkyrjaClient: MainUpdate loop triggered at: " + Utils.GetTimestamp(DateTime.UtcNow));

				DateTime frameTime = DateTime.UtcNow;

				if( !this.IsInitialized )
				{
					if( this.CoreConfig.Debug )
						Console.WriteLine("ValkyrjaClient: Initialized.");
					try
					{
						this.IsInitialized = true;
						await this.Events.Initialize();
					}
					catch( Exception exception )
					{
						await LogException(exception, "--Events.Initialize");
					}
				}

				if( this.DiscordClient.ConnectionState != ConnectionState.Connected ||
				    this.DiscordClient.LoginState != LoginState.LoggedIn ||
				    DateTime.Now - this.TimeConnected < TimeSpan.FromSeconds(this.CoreConfig.InitialUpdateDelay) )
				{
					await Task.Delay(10000);
					continue;
				}

				if( !this.IsConnected )
				{
					try
					{
						this.IsConnected = true;
						await this.Events.Connected();
					}
					catch( Exception exception )
					{
						await LogException(exception, "--Events.Connected");
					}

					continue; //Don't run update in the same loop as init.
				}

				try
				{
					if( this.CoreConfig.Debug )
						Console.WriteLine("ValkyrjaClient: Update loop triggered at: " + Utils.GetTimestamp(DateTime.UtcNow));

					this.Monitoring?.Uptime.Set((DateTime.UtcNow - this.TimeStarted).TotalSeconds);
					await Update();
				}
				catch( Exception exception )
				{
					await LogException(exception, "--Update");
				}

				TimeSpan deltaTime = DateTime.UtcNow - frameTime;
				if( this.CoreConfig.Debug )
					Console.WriteLine($"ValkyrjaClient: MainUpdate loop took: {deltaTime.TotalMilliseconds} ms");
				await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(this.CoreConfig.TotalShards * 1000, (TimeSpan.FromSeconds(1f / this.CoreConfig.TargetFps) - deltaTime).TotalMilliseconds)));
			}
		}

		private async Task Update()
		{
			if( this.CoreConfig.Debug )
				Console.WriteLine("ValkyrjaClient: UpdateModules loop triggered at: " + Utils.GetTimestamp(DateTime.UtcNow));
			await UpdateModules();
		}

//Modules
		private async Task InitModules()
		{
			List<Command> newCommands;
			foreach( IModule module in this.Modules )
			{
				try
				{
					module.HandleException += async (e, d, id) =>
						await LogException(e, "--Module." + module.ToString() + " | " + d, id);
					newCommands = module.Init(this);

					foreach( Command cmd in newCommands )
					{
						string cmdLowercase = cmd.Id.ToLower();
						if( this.Commands.ContainsKey(cmdLowercase) )
						{
							this.Commands[cmdLowercase] = cmd;
							continue;
						}

						this.Commands.Add(cmdLowercase, cmd);
					}
				}
				catch( Exception exception )
				{
					await LogException(exception, "--ModuleInit." + module.ToString());
				}
			}
		}

		private async Task UpdateModules()
		{
			IEnumerable<IModule> modules = this.Modules.Where(m => m.DoUpdate);
			foreach( IModule module in modules )
			{
				if( this.CoreConfig.Debug )
					Console.WriteLine($"ValkyrjaClient: ModuleUpdate.{module.ToString()} triggered at: " + Utils.GetTimestamp(DateTime.UtcNow));

				DateTime frameTime = DateTime.UtcNow;

				if( this.DiscordClient.ConnectionState != ConnectionState.Connected ||
				    this.DiscordClient.LoginState != LoginState.LoggedIn )
					break;

				try
				{
					await module.Update(this);
				}
				catch( Exception exception )
				{
					await LogException(exception, "--ModuleUpdate." + module.ToString());
				}

				if( this.CoreConfig.Debug )
					Console.WriteLine($"ValkyrjaClient: ModuleUpdate.{module.ToString()} took: {(DateTime.UtcNow - frameTime).TotalMilliseconds} ms");
			}
		}

//Commands
		private void GetCommandAndParams(string message, out string commandString, out string trimmedMessage, out string[] parameters)
		{
			trimmedMessage = "";
			parameters = null;

			MatchCollection regexMatches = this.RegexCommandParams.Matches(message);
			if( regexMatches.Count == 0 )
			{
				commandString = message.Trim();
				return;
			}

			commandString = regexMatches[0].Value;

			if( regexMatches.Count > 1 )
			{
				trimmedMessage = message.Substring(regexMatches[1].Index).Trim('\"', ' ', '\n');
				Match[] matches = new Match[regexMatches.Count];
				regexMatches.CopyTo(matches, 0);
				parameters = matches.Skip(1).Select(p => p.Value).ToArray();
				for( int i = 0; i < parameters.Length; i++ )
					parameters[i] = parameters[i].Trim('"');
			}
		}

		private async Task<bool> HandleCommand(Server server, SocketTextChannel channel, SocketMessage message, string prefix)
		{
			GetCommandAndParams(message.Content.Substring(prefix.Length), out string commandString, out string trimmedMessage, out string[] parameters);
			string originalCommandString = commandString;

			if( this.CoreConfig.Debug )
				Console.WriteLine($"Command: {commandString} | {trimmedMessage}");

			commandString = commandString.ToLower();

			if( server.Commands.ContainsKey(commandString) ||
			    (server.CustomAliases.ContainsKey(commandString) &&
			     server.Commands.ContainsKey(commandString = server.CustomAliases[commandString].CommandId.ToLower())) )
			{
				Command command = server.Commands[commandString];
				if( command.IsAlias && !string.IsNullOrEmpty(command.ParentId) ) //Internal, not-custom alias.
					command = server.Commands[command.ParentId.ToLower()];

				CommandArguments args = new CommandArguments(this, command, server, channel, message, originalCommandString, trimmedMessage, parameters, server.GetCommandOptions(command.Id));

				if( command.CanExecute(this, server, channel, message.Author as SocketGuildUser) )
					return await command.Execute(args);
			}
			else if( server.CustomCommands.ContainsKey(commandString) ||
			         (server.CustomAliases.ContainsKey(commandString) &&
			          server.CustomCommands.ContainsKey(commandString = server.CustomAliases[commandString].CommandId.ToLower())) )
			{
				CustomCommand customCommand = server.CustomCommands[commandString];
				if( customCommand.CanExecute(this, server, channel, message.Author as SocketGuildUser) )
					return await HandleCustomCommand(server, customCommand, server.GetCommandOptions(customCommand.CommandId), channel, message);
			}

			return false;
		}

		private async Task<bool> HandleCustomCommand(Server server, CustomCommand cmd, CommandOptions commandOptions, SocketTextChannel channel, SocketMessage message)
		{
			try
			{
				if( commandOptions != null && commandOptions.DeleteRequest &&
				    channel.Guild.CurrentUser.GuildPermissions.ManageMessages && !message.Deleted )
					await message.DeleteAsync();
			}
			catch( HttpException exception )
			{
				await server.HandleHttpException(exception, $"Failed to delete the command message in <#{channel.Id}>");
			}
			catch( Exception exception )
			{
				await LogException(exception, "HandleCustomCommand - delete request", server.Id);
			}

//todo - rewrite using string builder...
			string msg = cmd.Response;

			if( msg.Contains("{sender}") )
			{
				msg = msg.Replace("{{sender}}", "<@{0}>").Replace("{sender}", "<@{0}>");
				msg = string.Format(msg, message.Author.Id);
			}

			if( msg.Contains("{mentioned}") && message.MentionedUsers != null )
			{
				string mentions = "";
				SocketUser[] mentionedUsers = message.MentionedUsers.ToArray();
				for( int i = 0; i < mentionedUsers.Length; i++ )
				{
					if( i != 0 )
						mentions += (i == mentionedUsers.Length - 1) ? " and " : ", ";

					mentions += "<@" + mentionedUsers[i].Id + ">";
				}

				if( string.IsNullOrEmpty(mentions) )
				{
					msg = msg.Replace("{{mentioned}}", "Nobody").Replace("{mentioned}", "Nobody");
				}
				else
				{
					msg = msg.Replace("{{mentioned}}", "{0}").Replace("{mentioned}", "{0}");
					msg = string.Format(msg, mentions);
				}
			}

			if( this.CoreConfig.IgnoreEveryone )
				msg = msg.Replace("@everyone", "@-everyone").Replace("@here", "@-here");

			Match match = this.RegexCustomCommandPmAll.Match(msg);
			if( match.Success )
			{

				List<SocketUser> toPm = new List<SocketUser>();
				string pm = msg;
				msg = "It is nao sent via PM.";

				if( this.RegexCustomCommandPmMentioned.IsMatch(pm) && message.MentionedUsers != null && message.MentionedUsers.Any() )
					toPm.AddRange(message.MentionedUsers);
				else
					toPm.Add(message.Author);

				pm = pm.Substring(match.Value.Length).Trim();

				foreach( SocketUser user in toPm )
				{
					try
					{
						await user.SendMessageSafe(pm);
					}
					catch( Exception )
					{
						msg = "I'm sorry, I couldn't send the message. Either I'm blocked, or it's _**that** privacy option._";
						break;
					}
				}
			}

			MatchCollection matches = this.RngRegex.Matches(msg);
			if( matches.Count > 1 )
				msg = matches[Utils.Random.Next(0, matches.Count)].Value;


			if( cmd.MentionsEnabled )
				await channel.SendMessageSafe(msg, allowedMentions: AllowedMentions.All);
			else
				await channel.SendMessageSafe(msg);

			return true;
		}


// Guild events
		private async Task OnGuildJoined(SocketGuild guild)
		{
			try
			{
				await OnGuildAvailable(guild);
			}
			catch( Exception exception )
			{
				await LogException(exception, "--OnGuildJoined", guild.Id);
			}
		}

		private Task OnGuildUpdated(SocketGuild originalGuild, SocketGuild updatedGuild)
		{
			if( !this.Servers.ContainsKey(originalGuild.Id) )
				return Task.CompletedTask;

			this.Servers[originalGuild.Id].Guild = updatedGuild;
			return Task.CompletedTask;
		}

		private async Task OnGuildLeft(SocketGuild guild)
		{
			try
			{
				if( !this.Servers.ContainsKey(guild.Id) )
					return;

				for( int i = this.CurrentOperations.Count - 1; i >= 0; i-- )
				{
					if( this.CurrentOperations[i].CommandArgs.Server.Id == guild.Id )
						this.CurrentOperations[i].Cancel();
				}

				this.Servers.Remove(guild.Id);
			}
			catch( Exception exception )
			{
				await LogException(exception, "--OnGuildLeft", guild.Id);
			}
		}

		private async Task OnGuildAvailable(SocketGuild guild)
		{
			try
			{
				while( !this.IsInitialized )
					await Task.Delay(1000);

				this.GuildAvailableLock.Wait();

				Server server;
				if( this.Servers.ContainsKey(guild.Id) )
				{
					server = this.Servers[guild.Id];
					await server.ReloadConfig(this, this.Commands);
				}
				else
				{
					server = new Server(guild);
					await server.LoadConfig(this, this.Commands);
					this.Servers.Add(server.Id, server);
				}
			}
			catch( Exception exception )
			{
				await LogException(exception, "--OnGuildAvailable", guild.Id);
			}
			finally
			{
				this.GuildAvailableLock.Release();
			}
		}
	}
}
