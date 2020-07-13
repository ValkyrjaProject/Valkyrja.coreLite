using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Valkyrja.coreLite;
using Discord;
using Discord.Net;
using Discord.WebSocket;

using guid = System.UInt64;

namespace Valkyrja.entities
{
	public class Server
	{
		public class RoleConfig
		{
			public guid RoleId{ get; set; } = 0;
			public guid ServerId{ get; set; } = 0;
			public RolePermissionLevel PermissionLevel{ get; set; } = RolePermissionLevel.None;
		}
		public IValkyrjaClient Client;

		public readonly guid Id;

		public SocketGuild Guild;

		//public ServerConfig Config;
		public Dictionary<string, Command> Commands;
		public Dictionary<string, CustomCommand> CustomCommands = new Dictionary<string, CustomCommand>();
		public Dictionary<string, CustomAlias> CustomAliases = new Dictionary<string, CustomAlias>();
		//private CommandOptions CachedCommandOptions;
		//private List<CommandChannelOptions> CachedCommandChannelOptions;

		public ConcurrentDictionary<guid, guid> CommandReplyMsgIds = new ConcurrentDictionary<guid, guid>();

		public Dictionary<guid, RoleConfig> Roles;

		private int HttpExceptionCount = 0;


		public Server(SocketGuild guild)
		{
			this.Id = guild.Id;
			this.Guild = guild;
		}

		public Task ReloadConfig<T>(ValkyrjaClient<T> client, Dictionary<string, Command> allCommands) where T: Config, new()
		{
			this.Client = client;

			if( this.Commands?.Count != allCommands.Count )
			{
				this.Commands = new Dictionary<string, Command>(allCommands);
			}

			this.CustomCommands?.Clear();
			this.CustomAliases?.Clear();
			this.Roles?.Clear();

			//this.CustomCommands = dbContext.CustomCommands.AsQueryable().Where(c => c.ServerId == this.Id).ToDictionary(c => c.CommandId.ToLower());
			//this.CustomAliases = dbContext.CustomAliases.AsQueryable().Where(c => c.ServerId == this.Id).ToDictionary(c => c.Alias.ToLower());
			IEnumerable<RoleConfig> roles = client.Config.AdminRoleIds?.Where(id => id != 0).Select(id => new RoleConfig{ ServerId = this.Id, RoleId = id, PermissionLevel = RolePermissionLevel.Admin});
			roles = roles?.Concat(client.Config.ModeratorRoleIds?.Where(id => id != 0).Select(id => new RoleConfig{ ServerId = this.Id, RoleId = id, PermissionLevel = RolePermissionLevel.Moderator}) ?? new List<RoleConfig>());
			roles = roles?.Concat(client.Config.SubModeratorRoleIds?.Where(id => id != 0).Select(id => new RoleConfig{ ServerId = this.Id, RoleId = id, PermissionLevel = RolePermissionLevel.SubModerator}) ?? new List<RoleConfig>());
			this.Roles = roles?.ToDictionary(r => r.RoleId);

			return Task.CompletedTask;
		}

		public async Task LoadConfig<T>(ValkyrjaClient<T> client, Dictionary<string, Command> allCommands) where T: Config, new()
		{
			await ReloadConfig(client, allCommands);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public CommandOptions GetCommandOptions(string commandString)
		{
			return new CommandOptions();
			/*string lowerCommandString = commandString.ToLower();
			if( this.CustomAliases.ContainsKey(lowerCommandString) )
				commandString = this.CustomAliases[lowerCommandString].CommandId;

			if( this.CachedCommandOptions != null && this.CachedCommandOptions.CommandId == commandString )
				return this.CachedCommandOptions;

			/*ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
			this.CachedCommandOptions = dbContext.CommandOptions.AsQueryable().FirstOrDefault(c => c.ServerId == this.Id && c.CommandId == commandString);
			dbContext.Dispose();
			return this.CachedCommandOptions;*/
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public List<CommandChannelOptions> GetCommandChannelOptions(string commandString)
		{
			return new List<CommandChannelOptions>();
			/*CommandChannelOptions tmp;
			if( this.CachedCommandChannelOptions != null &&
			   (tmp = this.CachedCommandChannelOptions.FirstOrDefault()) != null && tmp.CommandId == commandString )
				return this.CachedCommandChannelOptions;

			ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
			this.CachedCommandChannelOptions = dbContext.CommandChannelOptions.AsQueryable().Where(c => c.ServerId == this.Id && c.CommandId == commandString)?.ToList();
			dbContext.Dispose();
			return this.CachedCommandChannelOptions;*/
		}

		///<summary> Returns the correct commandId if it exists, empty otherwise. Returns null if it is restricted command. </summary>
		public string GetCommandOptionsId(string commandString)
		{
			string commandId = "";
			commandString = commandString.ToLower();

			if( this.CustomAliases.ContainsKey(commandString) )
				commandString = this.CustomAliases[commandString].CommandId.ToLower();

			if( this.Commands.ContainsKey(commandString) )
			{
				Command command;
				if( (command = this.Commands[commandString]).IsCoreCommand ||
				    command.RequiredPermissions == PermissionType.OwnerOnly )
				{
					return null;
				}

				commandId = command.Id;
				if( command.IsAlias && !string.IsNullOrEmpty(command.ParentId) )
					commandId = command.ParentId;
			}
			else if( this.CustomCommands.ContainsKey(commandString) )
				commandId = this.CustomCommands[commandString].CommandId;

			return commandId;
		}

		public bool CanExecuteCommand(string commandId, int commandPermissions, SocketGuildChannel channel, SocketGuildUser user)
		{
			CommandOptions commandOptions = GetCommandOptions(commandId);
			List<CommandChannelOptions> commandChannelOptions = GetCommandChannelOptions(commandId);

			//Custom Command Channel Permissions
			CommandChannelOptions currentChannelOptions = null;
			if( commandPermissions != PermissionType.OwnerOnly &&
			    channel != null && commandChannelOptions != null &&
				(currentChannelOptions = commandChannelOptions.FirstOrDefault(c => c.ChannelId == channel.Id)) != null &&
			    currentChannelOptions.Blocked )
				return false;

			if( commandPermissions != PermissionType.OwnerOnly &&
			    channel != null && commandChannelOptions != null &&
			    commandChannelOptions.Any(c => c.Allowed) &&
			    ((currentChannelOptions = commandChannelOptions.FirstOrDefault(c => c.ChannelId == channel.Id)) == null ||
			    !currentChannelOptions.Allowed) )
				return false; //False only if there are *some* whitelisted channels, but it's not the current one.

			//Custom Command Permission Overrides
			if( commandOptions != null && commandOptions.PermissionOverrides != PermissionOverrides.Default )
			{
				switch(commandOptions.PermissionOverrides)
				{
					case PermissionOverrides.Nobody:
						return false;
					case PermissionOverrides.ServerOwner:
						commandPermissions = PermissionType.ServerOwner;
						break;
					case PermissionOverrides.Admins:
						commandPermissions = PermissionType.ServerOwner | PermissionType.Admin;
						break;
					case PermissionOverrides.Moderators:
						commandPermissions = PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator;
						break;
					case PermissionOverrides.SubModerators:
						commandPermissions = PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator | PermissionType.SubModerator;
						break;
					case PermissionOverrides.Members:
						commandPermissions = PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator | PermissionType.SubModerator | PermissionType.Member;
						break;
					case PermissionOverrides.Everyone:
						commandPermissions = PermissionType.Everyone;
						break;
					default:
						throw new ArgumentOutOfRangeException("permissionOverrides");
				}
			}

			//Actually check them permissions!
			return ((commandPermissions & PermissionType.Everyone) > 0) ||
			       ((commandPermissions & PermissionType.ServerOwner) > 0 && IsOwner(user)) ||
			       ((commandPermissions & PermissionType.Admin) > 0 && IsAdmin(user)) ||
			       ((commandPermissions & PermissionType.Moderator) > 0 && IsModerator(user)) ||
			       ((commandPermissions & PermissionType.SubModerator) > 0 && IsSubModerator(user)) ||
			       ((commandPermissions & PermissionType.Member) > 0 && IsMember(user));

		}

		public Embed GetManPage(Command command)
		{
			if( command.ManPage == null || command.RequiredPermissions == PermissionType.OwnerOnly )
				return null;

			return command.ManPage.ToEmbed(this, command);
		}


		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool IsOwner(SocketGuildUser user)
		{
			return this.Guild.OwnerId == user.Id || (user.GuildPermissions.ManageGuild && user.GuildPermissions.Administrator);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool IsAdmin(SocketGuildUser user)
		{
			return IsOwner(user) || user.Roles.Any(r => this.Client.Config.AdminRoleIds?.Contains(r.Id) ?? false);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool IsModerator(SocketGuildUser user)
		{
			return IsOwner(user) || user.Roles.Any(r => this.Client.Config.ModeratorRoleIds?.Contains(r.Id) ?? false);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool IsSubModerator(SocketGuildUser user)
		{
			return IsOwner(user) || user.Roles.Any(r => this.Client.Config.SubModeratorRoleIds?.Contains(r.Id) ?? false);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool IsMember(SocketGuildUser user)
		{
			return IsOwner(user) || user.Roles.Any(r => this.Roles.Any(p => p.Value.PermissionLevel >= RolePermissionLevel.Member && p.Value.RoleId == r.Id));
		}


		public SocketRole GetRole(string expression, out string response)
		{
			guid id = 0;
			IEnumerable<SocketRole> roles = this.Guild.Roles;
			IEnumerable<SocketRole> foundRoles = null;
			SocketRole role = null;

			if( !(guid.TryParse(expression, out id) && (role = this.Guild.GetRole(id)) != null) &&
			    !(foundRoles = roles.Where(r => r.Name == expression)).Any() &&
			    !(foundRoles = roles.Where(r => r.Name.ToLower() == expression.ToLower())).Any() &&
			    !(foundRoles = roles.Where(r => r.Name.ToLower().Contains(expression.ToLower()))).Any() )
			{
				response = "I did not find a role based on that expression.";
				return null;
			}

			if( foundRoles != null && foundRoles.Count() > 1 )
			{
				response = "I found more than one role with that expression, please be more specific.";
				return null;
			}

			if( role == null )
			{
				role = foundRoles.First();
			}

			response = "Done.";
			return role;
		}

		public async Task<bool> HandleHttpException(HttpException exception, string helptext = "")
		{
			string logMsg = "HttpException - further logging disabled";
			if( (int)exception.HttpCode >= 500 )
				logMsg = "DiscordPoop";
			else if( (int)exception.HttpCode == 404 )
				logMsg = "";
			else if( exception.Message.Contains("50007") )
				logMsg = "Failed to PM";
			else if( this.HttpExceptionCount < 5 )
			{
				try
				{
					string msg = $"Received error code `{(int)exception.HttpCode}`\n{helptext}\n\nPlease fix my permissions and channel access on your Discord Server `{this.Guild.Name}`.";
					SocketTextChannel channel = null;
					if( this.Client.Config.NotificationChannelId > 0 && (channel = this.Guild.GetTextChannel(this.Client.Config.NotificationChannelId)) != null )
					{
						await channel.SendMessageSafe(msg);
					}
					else
					{
						msg += "\n\nYou can also set these messages to be sent into a notification channel in the config.";
						await this.Client.SendPmSafe(this.Guild.Owner, msg);
					}
				}
				catch( Exception e )
				{
					if( !(e is HttpException) )
						await this.Client.LogException(e, "Server.HandleHttpException", this.Id);
				}
			}

			if( ++this.HttpExceptionCount > 1 )
			{
				logMsg = null;
			}

			if( !string.IsNullOrEmpty(logMsg) )
				await this.Client.LogException(exception, logMsg, this.Id);

			return this.HttpExceptionCount > 3;
		}
	}
}
