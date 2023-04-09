﻿using System;
using System.Collections;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Discord;
using Discord.Rest;
using Valkyrja.entities;
using Discord.WebSocket;
using guid = System.UInt64;

namespace Valkyrja.coreLite
{
	public partial class ValkyrjaClient<T>: IValkyrjaClient, IDisposable where T: BaseConfig, new()
	{
		private readonly Regex RegexMentionHelp = new Regex(".*(help|commands).*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
		private readonly Regex RegexPrefixHelp = new Regex(".*(command character|prefix).*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

		private async Task HandleMentionResponse(Server server, SocketTextChannel channel, SocketMessage message)
		{
			if( this.CoreConfig.Debug )
				Console.WriteLine("ValkyrjaClient: MentionReceived");

			string responseString = "";

			if( this.RegexMentionHelp.Match(message.Content).Success )
				responseString = $"Use `{this.CoreConfig.CommandPrefix}help` command to search for things, or the manual pages (`{this.CoreConfig.CommandPrefix}man`) for specific and detailed information about a command";
			else if( this.RegexPrefixHelp.Match(message.Content).Success )
				responseString = string.IsNullOrEmpty(this.CoreConfig.CommandPrefix) ? "Command prefix is empty. Someone forgot to set it up?" : $"Try this: `{this.CoreConfig.CommandPrefix}`";
			else
				responseString = "<:ValkyrjaNomPing:509482352028942358>";

			if( !string.IsNullOrEmpty(responseString) )
				await SendRawMessageToChannel(channel, responseString);
		}

		private async Task InitSlashCommands()
		{
			try
			{
				SlashCommandBuilder pingCommand = new SlashCommandBuilder().WithName("ping").WithDescription("Verify basic functionality.")
					.WithNameLocalizations(new Dictionary<string, string>()).WithDescriptionLocalizations(new Dictionary<string, string>()); //D.NET bug #2453
				await this.DiscordClient.CreateGlobalApplicationCommandAsync(pingCommand.Build());
			}
			catch( Exception e )
			{
				await LogException(e, "InitSlashCommands");
			}
		}

		private async Task ExecuteSlashCommand(SocketSlashCommand command)
		{
			if( command.CommandName == "ping" && command.GuildId.HasValue && this.Servers.ContainsKey(command.GuildId.Value) )
			{
				TimeSpan time = DateTime.UtcNow - Utils.GetTimeFromId(command.Id);
				await command.RespondAsync(GetStatusString(time, this.Servers[command.GuildId.Value]), ephemeral: true);
			}
		}

		private Task InitCommands()
		{
			Command newCommand = null;

// !restart
			newCommand = new Command("restart");
			newCommand.Type = CommandType.Standard;
			newCommand.IsCoreCommand = true;
			newCommand.Description = "Shut down the bot.";
			newCommand.ManPage = new ManPage("", "");
			newCommand.RequiredPermissions = PermissionType.OwnerOnly;
			newCommand.OnExecute += async e => {
				await e.SendReplySafe("bai");
				await Task.Delay(1000);
				Environment.Exit(0);
			};
			this.Commands.Add(newCommand.Id.ToLower(), newCommand);
			this.Commands.Add("shutdown", newCommand.CreateAlias("shutdown"));

// !operations
			newCommand = new Command("operations");
			newCommand.Type = CommandType.Standard;
			newCommand.IsCoreCommand = true;
			newCommand.Description = "Display info about all queued or running operations on your server.";
			newCommand.ManPage = new ManPage("", "");
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin;
			newCommand.OnExecute += async e => {
				StringBuilder response = new StringBuilder();
				bool allOperations = IsAdmin(e.Message.Author.Id);

				response.AppendLine($"Total operations in the queue: `{this.CurrentOperations.Count}`");
				if( allOperations )
					response.AppendLine($"Currently allocated data Memory: `{(GC.GetTotalMemory(false) / 1000000f):#0.00} MB`");

				response.AppendLine();
				lock( this.OperationsLock )
				{
					foreach( Operation op in this.CurrentOperations )
					{
						if( !allOperations && op.CommandArgs.Server.Id != e.Server.Id )
							continue;

						response.AppendLine(op.ToString());
						if( allOperations )
							response.AppendLine($"Server: `{op.CommandArgs.Server.Guild.Name}`\n" +
							                    $"ServerID: `{op.CommandArgs.Server.Id}`\n" +
							                    $"Allocated DataMemory: `{op.AllocatedMemoryStarted:#0.00} MB`\n");
					}
				}

				string responseString = response.ToString();
				if( string.IsNullOrEmpty(responseString) )
					responseString = "There are no operations running.";

				await e.SendReplySafe(responseString);
			};
			this.Commands.Add(newCommand.Id.ToLower(), newCommand);

// !cancel
			newCommand = new Command("cancel");
			newCommand.Type = CommandType.Standard;
			newCommand.IsCoreCommand = true;
			newCommand.Description = "Cancel queued or running operation - use in the same channel. (nuke, promoteEveryone, etc...)";
			newCommand.ManPage = new ManPage("<CommandId>", "`<CommandId>` - running operation type command which this will interrupt.");
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin;
			newCommand.OnExecute += async e => {
				string responseString = "Operation not found.";
				Operation operation = null;

				if( !string.IsNullOrEmpty(e.TrimmedMessage) &&
				    (operation = this.CurrentOperations.FirstOrDefault(
					    op => op.CommandArgs.Channel.Id == e.Channel.Id &&
					          op.CommandArgs.Command.Id == e.TrimmedMessage)) != null )
					responseString = "Operation canceled:\n\n" + operation.ToString();

				await e.SendReplySafe(responseString);
			};
			this.Commands.Add(newCommand.Id.ToLower(), newCommand);

// !say
			newCommand = new Command("say");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Make the bot say something!";
			newCommand.ManPage = new ManPage("<text>", "`<text>` - Text which the bot will repeat.");
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator | PermissionType.SubModerator;
			newCommand.DeleteRequest = true;
			newCommand.IsBonusCommand = true;
			newCommand.IsBonusAdminCommand = true;
			newCommand.IsSupportCommand = true;
			newCommand.OnExecute += async e => {
				if( string.IsNullOrWhiteSpace(e.TrimmedMessage) )
				{
					await e.SendReplySafe("Say what?");
					return;
				}

				await e.SendReplySafe(e.TrimmedMessage);
			};
			this.Commands.Add(newCommand.Id.ToLower(), newCommand);

// !edit
			newCommand = new Command("edit");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Edit a message the bot previously said!";
			newCommand.ManPage = new ManPage("<MessageId> <text>", "`<MessageId>` - An ID of a message that will be edited.\n\n`<text>` - Text which the bot will repeat.");
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator | PermissionType.SubModerator;
			newCommand.DeleteRequest = true;
			newCommand.IsBonusCommand = true;
			newCommand.IsBonusAdminCommand = true;
			newCommand.IsSupportCommand = true;
			newCommand.OnExecute += async e => {
				IMessage msg = null;
				if( e.MessageArgs == null || e.MessageArgs.Length < 2 || !guid.TryParse(e.MessageArgs[0], out guid id) || (msg = await e.Channel.GetMessageAsync(id)) == null )
				{
					await e.SendReplySafe("Edit what?");
					return;
				}

				switch( msg ) {
					case RestUserMessage message:
						await message?.ModifyAsync(m => m.Content = e.TrimmedMessage.Substring(e.MessageArgs[0].Length+1));
						break;
					case SocketUserMessage message:
						await message?.ModifyAsync(m => m.Content = e.TrimmedMessage.Substring(e.MessageArgs[0].Length+1));
						break;
					default:
						await e.SendReplySafe("GetMessage went bork.");
						break;
				}
			};
			this.Commands.Add(newCommand.Id.ToLower(), newCommand);

// !status
			newCommand = new Command("status");
			newCommand.Type = CommandType.Standard;
			newCommand.IsCoreCommand = true;
			newCommand.Description = "Display basic server status.";
			newCommand.ManPage = new ManPage("", "");
			newCommand.RequiredPermissions = PermissionType.Everyone;
			newCommand.OnExecute += async e => {
				TimeSpan time = DateTime.UtcNow - Utils.GetTimeFromId(e.Message.Id);
				string message = "";
				if( this.CoreConfig.IsValkyrjaHosted )
				{
					string cpuLoad = Bash.Run("grep 'cpu ' /proc/stat | awk '{print ($2+$4)*100/($2+$4+$5)}'");
					string memoryUsed = Bash.Run("free | grep Mem | awk '{print $3/$2 * 100.0}'");
					double memoryPercentage = double.Parse(memoryUsed);
					string[] temp = Bash.Run("sensors | egrep '(Tdie|Tctl)' | awk '{print $2}'").Split('\n');

					message += $"Service Status: <{this.CoreConfig.StatusPage}>\n" +
					           $"```md\n" +
					           $"[    Memory usage ][ {memoryPercentage:#00.00} % ({memoryPercentage / 100 * 128:000.00}/128 GB) ]\n" +
					           $"[        CPU Load ][ {double.Parse(cpuLoad):#00.00} % ({temp[1]})       ]\n" +
					           $"[        Shard ID ][ {this.CurrentShardId:00}                      ]\n" +
					           $"[ Discord Latency ][ {time.TotalMilliseconds:#00}                     ]\n```";
				}

				await e.SendReplySafe(message);
			};
			this.Commands.Add(newCommand.Id.ToLower(), newCommand);
			this.Commands.Add("ping", newCommand.CreateAlias("ping"));

// !man
			newCommand = new Command("man");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Show detailed manual page for a command.";
			newCommand.ManPage = new ManPage("<command>", "`<command>` - Command ID for which to display .");
			newCommand.RequiredPermissions = PermissionType.Everyone;
			newCommand.OnExecute += async e => {
				string commandId = e.TrimmedMessage.ToLower();
				string response = "I ain't got no real command like that. (This feature isn't a thing for Custom Commands!)";
				if( string.IsNullOrEmpty(commandId) || (!e.Server.Commands.ContainsKey(commandId) && (!e.Server.CustomAliases.ContainsKey(commandId) || !e.Server.Commands.ContainsKey(commandId = e.Server.CustomAliases[commandId].CommandId))) )
				{
					await e.SendReplySafe(response);
					return;
				}

				Command cmd = e.Server.Commands[commandId];
				if( !string.IsNullOrEmpty(cmd.ParentId) && e.Server.Commands.ContainsKey(cmd.ParentId) )
					cmd = e.Server.Commands[cmd.ParentId];
				Embed embed = e.Server.GetManPage(cmd);
				await e.Channel.SendMessageSafe(null, embed);
			};
			this.Commands.Add(newCommand.Id.ToLower(), newCommand);
			this.Commands.Add("manual", newCommand.CreateAlias("manual"));

// !help
			newCommand = new Command("help");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "PMs a list of Custom Commands for the server if used without arguments, or search for specific commands.";
			newCommand.ManPage = new ManPage("[search expression]", "[search expression] - Optional argument to search for specific commands.");
			newCommand.RequiredPermissions = PermissionType.Everyone;
			newCommand.OnExecute += async e => {
				await e.SendReplySafe($"`{this.CoreConfig.CommandPrefix}help` is merely a search. Use `{this.CoreConfig.CommandPrefix}man` for detailed manual page.");

				StringBuilder response = new StringBuilder();
				StringBuilder commandStrings = new StringBuilder();

				bool isSpecific = !string.IsNullOrWhiteSpace(e.TrimmedMessage);
				string prefix = this.CoreConfig.CommandPrefix;
				List<string> includedCommandIds = new List<string>();
				int count = 0;
				bool cantPm = false;

				async Task Append(string newString)
				{
					string pm = commandStrings.ToString();
					if( !isSpecific && pm.Length + newString.Length >= BaseConfig.MessageCharacterLimit )
					{
						try
						{
							if( this.CoreConfig.HelpPrintsEverything )
								await e.SendReplySafe(pm);
							else
								await e.Message.Author.SendMessageAsync(pm);
						}
						catch( Exception )
						{
							cantPm = true;
						}
						commandStrings.Clear();
					}

					commandStrings.AppendLine(newString);
				}

				async Task AddCustomAlias(string commandId)
				{
					string newString = "";
					List<CustomAlias> aliases = e.Server.CustomAliases.Values.Where(a => a.CommandId == commandId).ToList();
					int aliasCount = aliases.Count;
					if( aliasCount > 0 )
					{
						newString = aliasCount == 1 ? " **-** Custom Alias: " : " **-** Custom Aliases: ";
						for( int i = 0; i < aliasCount; i++ )
							newString += $"{(i == 0 ? "`" : i == aliasCount - 1 ? " and `" : ", `")}{prefix}{aliases[i].Alias}`";

						await Append(newString);
					}
				}

				async Task AddCommand(Command cmd)
				{
					if( includedCommandIds.Contains(cmd.Id) )
						return;
					includedCommandIds.Add(cmd.Id);

					string newString = $"\n```diff\n{(cmd.CanExecute(this, e.Server, e.Channel, e.Message.Author as SocketGuildUser) ? "+" : "-")}" +
					                   $"  {prefix}{cmd.Id}```" +
					                   $" **-** {cmd.Description}";
					if( cmd.Aliases != null && cmd.Aliases.Any() )
					{
						int aliasCount = cmd.Aliases.Count;
						newString += aliasCount == 1 ? "\n **-** Alias: " : "\n **-** Aliases: ";
						for( int i = 0; i < aliasCount; i++ )
							newString += $"{(i == 0 ? "`" : i == aliasCount - 1 ? " and `" : ", `")}{prefix}{cmd.Aliases[i]}`";
					}

					await Append(newString);
					await AddCustomAlias(cmd.Id);
				}

				async Task  AddCustomCommand(CustomCommand cmd)
				{
					if( includedCommandIds.Contains(cmd.CommandId) )
						return;
					includedCommandIds.Add(cmd.CommandId);

					string newString = $"\n```diff\n{(cmd.CanExecute(this, e.Server, e.Channel, e.Message.Author as SocketGuildUser) ? "+" : "-")}" +
					                   $"  {prefix}{cmd.CommandId}```";
					if( !string.IsNullOrWhiteSpace(cmd.Description) )
						newString += $"\n **-** {cmd.Description}";

					await Append(newString);
					await AddCustomAlias(cmd.CommandId);
				}

				if( isSpecific )
				{
					string expression = e.TrimmedMessage.Replace(" ", "|") + ")\\w*";
					if( e.MessageArgs.Length > 1 )
						expression += "(" + expression;
					Regex regex = new Regex($"\\w*({expression}", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(10f));

					foreach( Command cmd in e.Server.Commands.Values )
					{
						if( !cmd.IsHidden &&
						    cmd.RequiredPermissions != PermissionType.OwnerOnly &&
						    regex.Match(cmd.Id).Success )
						{
							Command command = cmd;
							if( cmd.IsAlias && e.Server.Commands.ContainsKey(cmd.ParentId.ToLower()) )
								command = e.Server.Commands[cmd.ParentId.ToLower()];

							if( includedCommandIds.Contains(command.Id) )
								continue;

							if( ++count > 5 )
								break;

							await AddCommand(cmd);
						}
					}

					foreach( CustomCommand cmd in e.Server.CustomCommands.Values )
					{
						if( regex.Match(cmd.CommandId).Success ) //Chances are that it's gonna fail more often.
						{
							if( ++count > 5 )
								break;

							await AddCustomCommand(cmd);
						}
					}

					foreach( CustomAlias alias in e.Server.CustomAliases.Values )
					{
						if( regex.Match(alias.Alias).Success ) //Chances are that it's gonna fail more often.
						{
							if( ++count > 5 )
								break;

							if( e.Server.Commands.ContainsKey(alias.CommandId.ToLower()) )
								await AddCommand(e.Server.Commands[alias.CommandId.ToLower()]);
							else if( e.Server.CustomCommands.ContainsKey(alias.CommandId.ToLower()) )
							{
								await AddCustomCommand(e.Server.CustomCommands[alias.CommandId.ToLower()]);
							}
						}
					}

					if( count == 0 )
						response.AppendLine("I did not find any commands matching your search expression.");
					else
					{
						if( count > 5 )
							response.AppendLine("I found too many commands matching your search expression. **Here are the first five:**");

						response.Append(commandStrings.ToString());
					}
				}
				else if( this.CoreConfig.HelpPrintsEverything )
				{
					foreach( Command cmd in e.Server.Commands.Values.Where(cmd => !cmd.IsAlias) )
					{
						await AddCommand(cmd);
					}

					response.Append(commandStrings.ToString());
				}
				else if( e.Server.CustomCommands.Any() ) //Not specific - PM CustomCommands.
				{
					foreach( CustomCommand cmd in e.Server.CustomCommands.Values )
					{
						await AddCustomCommand(cmd);
					}

					try
					{
						await e.Message.Author.SendMessageSafe(commandStrings.ToString());
						response.AppendLine("I've PMed you the Custom Commands for this server.");
					}
					catch( Exception )
					{
						cantPm = true;
					}
				}

				if( cantPm )
					response.AppendLine("And I was unable to PM you the Custom Commands for this server. (Fix your privacy settings or unblock me.)");

				await e.SendReplySafe(response.ToString());
			};
			this.Commands.Add(newCommand.Id.ToLower(), newCommand);

// !alias
			/*newCommand = new Command("alias");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Manage command aliases, use without parameters for more details.";
			newCommand.ManPage = new ManPage("<list|create|delete> [alias] [command]", "`list` - Display the list of custom aliases.\n\n`create alias command` - Create a new `alias` to the `command`.\n\n`delete alias` - Delete the `alias`.");
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin;
			newCommand.OnExecute += async e => {
				if(e.MessageArgs == null || e.MessageArgs.Length == 0 || !(
					   (e.MessageArgs.Length == 1 && e.MessageArgs[0] == "list") ||
					   (e.MessageArgs.Length == 2 && (e.MessageArgs[0] == "remove" || e.MessageArgs[0] == "delete")) ||
					   (e.MessageArgs.Length == 3 && (e.MessageArgs[0] == "add" || e.MessageArgs[0] == "create")) ))
				{
					await e.SendReplySafe(string.Format(
						"Use this command with the following parameters:\n" +
						"  `{0}{1} list` - Display the list of your custom aliases.\n" +
						"  `{0}{1} create alias command` - Create a new `alias` to the old `command`.\n" +
						"  `{0}{1} delete alias` - Delete the `alias`.\n",
						e.Server.Config.CommandPrefix,
						e.Command.Id
					));
					return;
				}
				string responseString = "";

				switch(e.MessageArgs[0])
				{
					case "list":
					{
						if( e.Server.CustomAliases == null || !e.Server.CustomAliases.Any() )
						{
							responseString = "There aren't any! O_O";
							break;
						}

						StringBuilder response = new StringBuilder();
						response.AppendLine("Command-Aliases on this server:\n```http\nexampleAlias: command\n---------------------");
						foreach( CustomAlias alias in e.Server.CustomAliases.Values )
						{
							string line = alias.Alias + ": " + alias.CommandId;
							if( line.Length + response.Length + 5 > GlobalConfig.MessageCharacterLimit )
							{
								await e.SendReplySafe(response.ToString() + "\n```");
								response.Clear().AppendLine("```http\nexampleAlias: command\n---------------------");
							}
							response.AppendLine(line);
						}

						response.Append("```");
						responseString = response.ToString();
					}
						break;
					case "create":
					case "add":
					{
						CustomAlias alias = new CustomAlias(){
							Alias = e.MessageArgs[1],
							CommandId = e.MessageArgs[2],
							ServerId = e.Server.Id
						};
						if( e.Server.Commands.ContainsKey(alias.Alias.ToLower()) ||
						    e.Server.CustomCommands.ContainsKey(alias.Alias.ToLower()) ||
						    e.Server.CustomAliases.ContainsKey(alias.Alias.ToLower()) )
						{
							responseString = $"I already have a command with this name (`{alias.Alias}`)";
							break;
						}
						if( !e.Server.Commands.ContainsKey(alias.CommandId.ToLower()) &&
						    !e.Server.CustomCommands.ContainsKey(alias.CommandId.ToLower()) )
						{
							responseString = $"Target command not found (`{alias.CommandId}`)";
							break;
						}

						if( e.Server.Commands.ContainsKey(alias.CommandId.ToLower()) )
						{
							Command cmd = e.Server.Commands[alias.CommandId.ToLower()];
							if( cmd.IsAlias && !string.IsNullOrEmpty(cmd.ParentId) )
								alias.CommandId = cmd.ParentId;
						}

						ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
						dbContext.CustomAliases.Add(alias);
						dbContext.SaveChanges();
						dbContext.Dispose();

						responseString = $"Alias `{e.Server.Config.CommandPrefix}{alias.Alias}` created.";
					}
						break;
					case "delete":
					case "remove":
					{
						if( e.Server.CustomAliases == null || !e.Server.CustomAliases.ContainsKey(e.MessageArgs[1].ToLower()) )
						{
							responseString = $"Alias not found. (`{e.MessageArgs[1]}`)";
							break;
						}

						ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
						CustomAlias alias = new CustomAlias{ServerId = e.Server.Id, Alias = e.MessageArgs[1]};
						dbContext.CustomAliases.Attach(alias);
						dbContext.CustomAliases.Remove(alias);
						dbContext.SaveChanges();
						dbContext.Dispose();

						responseString = $"RIP `{e.Server.Config.CommandPrefix}{alias.Alias}`.";
					}
						break;
					default:
						responseString = "Unknown property.";
						return;
				}

				await e.SendReplySafe(responseString);
			};
			this.Commands.Add(newCommand.Id.ToLower(), newCommand);*/

// !listPermissions
			/*newCommand = new Command("listPermissions");
			newCommand.Type = CommandType.Standard;
			newCommand.IsCoreCommand = true;
			newCommand.Description = "List permissions of all the commands.";
			newCommand.ManPage = new ManPage("", "");
			newCommand.RequiredPermissions = PermissionType.ServerOwner;
			newCommand.OnExecute += async e => {
				string response = "";
				ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
				StringBuilder responseBuilder = new StringBuilder();
				foreach( Command cmd in this.Commands.Values.OrderBy(c => c.RequiredPermissions) )
				{
					if( cmd.IsAlias || cmd.IsHidden || cmd.IsCoreCommand || cmd.RequiredPermissions == PermissionType.OwnerOnly )
						continue;

					CommandOptions options = dbContext.GetOrAddCommandOptions(e.Server, cmd.Id);
					responseBuilder.Append($"`{cmd.Id}`: `{options.PermissionOverrides.ToString()}`");
					if( options.PermissionOverrides == PermissionOverrides.Default )
					{
						PermissionOverrides permissions = PermissionOverrides.Default;
						switch(cmd.RequiredPermissions)
						{
							case PermissionType.ServerOwner:
								permissions = PermissionOverrides.ServerOwner;
								break;
							case PermissionType.ServerOwner | PermissionType.Admin:
								permissions = PermissionOverrides.Admins;
								break;
							case PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator:
								permissions = PermissionOverrides.Moderators;
								break;
							case PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator | PermissionType.SubModerator:
								permissions = PermissionOverrides.SubModerators;
								break;
							case PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator | PermissionType.SubModerator | PermissionType.Member:
								permissions = PermissionOverrides.Members;
								break;
							default:
								permissions = PermissionOverrides.Everyone;
								break;
						}
						responseBuilder.AppendLine($" -> `{permissions.ToString()}`");
					}
					else
						responseBuilder.AppendLine();
				}
				response = responseBuilder.ToString();
				dbContext.Dispose();
				await e.SendReplySafe(response);
			};
			this.Commands.Add(newCommand.Id.ToLower(), newCommand);*/

// !permissions
			/*newCommand = new Command("permissions");
			newCommand.Type = CommandType.Standard;
			newCommand.IsCoreCommand = true;
			newCommand.Description = "Configure permission groups for every command. Use without parameters for help.";
			newCommand.ManPage = new ManPage("<CommandId> [PermissionGroup]", "`<CommandId>` - name of the command for which you would like to display or change permissions.\n\n" +
			                                                                  "`[PermissionGroup]` - Optional argument to change the permissions of the CommandId - one of:\n" +
			                                                                  "*    `ServerOwner`, `Admins`, `Moderators`, `SubModerators` or `Members`, which are configured on the website.\n" +
			                                                                  "*    `Everyone`, `Nobody` and `Default` (will set default permissions as seen in the webdocs.)\n");
			newCommand.RequiredPermissions = PermissionType.ServerOwner;
			newCommand.OnExecute += async e => {
				string response = string.Format(
					"Use this command with the following parameters:\n" +
					"  `{0}{1} CommandID PermissionGroup` - where `CommandID` is name of the command, and `PermissionGroups` can be:\n" +
					"    `ServerOwner`, `Admins`, `Moderators`, `SubModerators`, `Members`, `Everyone` - Look at the docs for reference: <https://valkyrja.app/docs>\n" +
					"    `Nobody` - Block this command from execusion even by Server Owner.\n" +
					"    `Default` - will set default permissions as seen in the docs.\n"+
					"  For example `{0}{1} nuke ServerOwner`",
					e.Server.Config.CommandPrefix,
					e.Command.Id);

				if( e.MessageArgs == null  || e.MessageArgs.Length < 1)
				{
					await e.SendReplySafe(response);
					return;
				}

				string commandId = "";
				ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
				if( string.IsNullOrEmpty(commandId = e.Server.GetCommandOptionsId(e.MessageArgs[0])) )
				{
					if( commandId == null )
					{
						response = "I'm sorry but you can not restrict this command.";
					}
					else if( commandId == "" )
					{
						response = $"Command `{e.MessageArgs[0]}` not found.";
					}
				}
				else if( e.MessageArgs.Length == 1 )
				{
					StringBuilder responseBuilder = new StringBuilder();
					CommandOptions options = dbContext.GetOrAddCommandOptions(e.Server, commandId);
					responseBuilder.Append($"Current permissions for `{commandId}` are:\n" +
					                       $"`{options.PermissionOverrides.ToString()}`");
					if( options.PermissionOverrides == PermissionOverrides.Default )
					{
						Command command = null;
						CustomCommand customCommand = null;
						if( e.Server.Commands.ContainsKey(commandId.ToLower()) && (command = e.Server.Commands[commandId.ToLower()]) != null )
						{
							PermissionOverrides permissions = PermissionOverrides.Default;
							switch(command.RequiredPermissions)
							{
								case PermissionType.ServerOwner:
									permissions = PermissionOverrides.ServerOwner;
									break;
								case PermissionType.ServerOwner | PermissionType.Admin:
									permissions = PermissionOverrides.Admins;
									break;
								case PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator:
									permissions = PermissionOverrides.Moderators;
									break;
								case PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator | PermissionType.SubModerator:
									permissions = PermissionOverrides.SubModerators;
									break;
								case PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator | PermissionType.SubModerator | PermissionType.Member:
									permissions = PermissionOverrides.Members;
									break;
								default:
									permissions = PermissionOverrides.Everyone;
									break;
							}
							responseBuilder.Append($" -> `{permissions.ToString()}`");
						}
						else if( e.Server.CustomCommands.ContainsKey(commandId.ToLower()) && (customCommand = e.Server.CustomCommands[commandId.ToLower()]) != null )
							responseBuilder.Append($" -> `{PermissionOverrides.Everyone}`");
					}

					if( options.DeleteReply && options.DeleteRequest )
						responseBuilder.Append("\n+ This command will attempt to delete both, the message that issued the command and my response.");
					else if( options.DeleteReply )
						responseBuilder.Append("\n+ This command will attempt to delete my response.");
					else if( options.DeleteRequest )
						responseBuilder.Append("\n+ This command will attempt to delete the message that issued the command.");

					IEnumerable<CommandChannelOptions> channelBlacklist = dbContext.CommandChannelOptions.AsQueryable().Where(c => c.ServerId == e.Server.Id && c.CommandId == commandId && c.Blacklisted);
					IEnumerable<CommandChannelOptions> channelWhitelist = dbContext.CommandChannelOptions.AsQueryable().Where(c => c.ServerId == e.Server.Id && c.CommandId == commandId && c.Whitelisted);
					if( channelBlacklist.Any() )
					{
						responseBuilder.Append("\n+ This command can not be invoked in any of the following channels:");
						foreach( CommandChannelOptions channelOptions in channelBlacklist )
						{
							responseBuilder.Append($"\n    <#{channelOptions.ChannelId}>");
						}
					}
					if( channelWhitelist.Any() )
					{
						responseBuilder.Append("\n+ This command can be invoked only in the following channels:");
						foreach( CommandChannelOptions channelOptions in channelWhitelist )
						{
							if( channelBlacklist.Any(c => c.ChannelId == channelOptions.ChannelId) )
								continue;
							responseBuilder.Append($"\n<#{channelOptions.ChannelId}>");
						}
					}

					response = responseBuilder.ToString();
				}
				else if( e.MessageArgs.Length == 2 && Enum.TryParse(e.MessageArgs[1], true, out PermissionOverrides permissionOverrides) )
				{
					CommandOptions options = dbContext.GetOrAddCommandOptions(e.Server, commandId);
					options.PermissionOverrides = permissionOverrides;
					dbContext.SaveChanges();
					response = "All set!";
				}

				dbContext.Dispose();

				await e.SendReplySafe(response);
			};
			this.Commands.Add(newCommand.Id.ToLower(), newCommand);*/

// !deleteRequest
			/*newCommand = new Command("deleteRequest");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Set a command to have the issuing request message deleted automatically.";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin;
			newCommand.ManPage = new ManPage("<CommandId> <true|false>", "`<CommandId>` - The command for which to change the delete settings.\n\n`<true|false>` - True to delete the issuing request message.");
			newCommand.OnExecute += async e => {
				if( e.MessageArgs == null || e.MessageArgs.Length < 2 ||
				    !bool.TryParse(e.MessageArgs[1], out bool deleteRequest) )
				{
					await e.SendReplySafe("Invalid parameters...\n" + e.Command.Description);
					return;
				}

				string commandId = e.Server.GetCommandOptionsId(e.MessageArgs[0]);
				if( commandId == null )
				{
					await e.SendReplySafe("I'm sorry but you can not restrict this command.");
					return;
				}
				if( commandId == "" )
				{
					await e.SendReplySafe($"Command `{e.MessageArgs[0]}` not found.");
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
				CommandOptions options = dbContext.GetOrAddCommandOptions(e.Server, commandId);

				options.DeleteRequest = deleteRequest;

				dbContext.SaveChanges();
				dbContext.Dispose();

				await e.SendReplySafe("Okay...");
			};
			this.Commands.Add(newCommand.Id.ToLower(), newCommand);
			this.Commands.Add("removerequest", newCommand.CreateAlias("removeRequest"));*/

// !deleteReply
			/*newCommand = new Command("deleteReply");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Set a command to delete bot's replies in a few seconds. _(Only some commands support this, if you find one that doesn't, submit a feature request!)_";
			newCommand.ManPage = new ManPage("<CommandId> <true|false>", "`<CommandId>` - The command for which to change the delete settings.\n\n`<true|false>` - True to delete the bots response message.");
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin;
			newCommand.OnExecute += async e => {
				if( e.MessageArgs == null || e.MessageArgs.Length < 2 ||
				    !bool.TryParse(e.MessageArgs[1], out bool deleteReply) )
				{
					await e.SendReplySafe("Invalid parameters...\n" + e.Command.Description);
					return;
				}

				string commandId = e.Server.GetCommandOptionsId(e.MessageArgs[0]);
				if( commandId == null )
				{
					await e.SendReplySafe("I'm sorry but you can not restrict this command.");
					return;
				}
				if( commandId == "" )
				{
					await e.SendReplySafe($"Command `{e.MessageArgs[0]}` not found.");
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
				CommandOptions options = dbContext.GetOrAddCommandOptions(e.Server, commandId);

				options.DeleteReply = deleteReply;

				dbContext.SaveChanges();
				dbContext.Dispose();

				await e.SendReplySafe("Okay...");
			};
			this.Commands.Add(newCommand.Id.ToLower(), newCommand);
			this.Commands.Add("removereply", newCommand.CreateAlias("removeReply"));*/

// !cmdChannelWhitelist
			/*newCommand = new Command("cmdChannelWhitelist");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Allow a command to be ran only in certain channels.";
			newCommand.ManPage = new ManPage("<CommandId> <add|remove> <ChannelId>", "`<CommandId>` - The command for which to change the restriction settings.\n\n`<add|remove>` - Add or remove to/from the restriction list.\n\n`<ChannelId>` - Id or mention of the channel in which to allow the execution of the Command");
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin;
			newCommand.OnExecute += async e => {
				if( e.MessageArgs == null || e.MessageArgs.Length < 2 ||
				    (e.MessageArgs[1].ToLower() != "add" && e.MessageArgs[1].ToLower() != "remove") ||
				    !guid.TryParse(e.MessageArgs[2].Trim('<', '#', '>'), out guid channelId) || e.Server.Guild.GetChannel(channelId) == null )
				{
					await e.SendReplySafe("Invalid parameters...\n" + e.Command.Description);
					return;
				}

				string commandId = e.Server.GetCommandOptionsId(e.MessageArgs[0]);
				if( commandId == null )
				{
					await e.SendReplySafe("I'm sorry but you can not restrict this command.");
					return;
				}
				if( commandId == "" )
				{
					await e.SendReplySafe($"Command `{e.MessageArgs[0]}` not found.");
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
				CommandChannelOptions commandOptions = dbContext.GetOrAddCommandChannelOptions(e.Server.Id, channelId, commandId);

				string responseString = "Success! \\o/";
				switch(e.MessageArgs[1].ToLower())
				{
					case "add":
						commandOptions.Allowed = true;
						dbContext.SaveChanges();
						break;
					case "remove":
						commandOptions.Allowed = false;
						dbContext.SaveChanges();
						break;
					default:
						responseString = "Invalid parameters...\n" + e.Command.Description;
						break;
				}

				dbContext.Dispose();
				await e.SendReplySafe(responseString);
			};
			this.Commands.Add(newCommand.Id.ToLower(), newCommand);

// !cmdChannelWhitelistAllCC
			newCommand = new Command("cmdChannelWhitelistAllCC");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Allow all custom commands to be ran only in certain channels.";
			newCommand.ManPage = new ManPage("<add|remove> <ChannelId>", "`<add|remove>` - Add or remove to/from the restriction list.\n\n`<ChannelId>` - Id or mention of the channel in which to allow the execution of the CustomCommands");
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin;
			newCommand.OnExecute += async e => {
				if( e.MessageArgs == null || e.MessageArgs.Length < 2 ||
				    (e.MessageArgs[0].ToLower() != "add" && e.MessageArgs[0].ToLower() != "remove") ||
				    !guid.TryParse(e.MessageArgs[1].Trim('<', '#', '>'), out guid channelId) || e.Server.Guild.GetChannel(channelId) == null )
				{
					await e.SendReplySafe("Invalid parameters...\n" + e.Command.Description);
					return;
				}

				string responseString = "Success! \\o/";
				ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
				foreach( CustomCommand cmd in e.Server.CustomCommands.Values )
				{
					CommandChannelOptions commandOptions = dbContext.GetOrAddCommandChannelOptions(e.Server.Id, channelId, cmd.CommandId);

					switch( e.MessageArgs[0].ToLower() )
					{
						case "add":
							commandOptions.Allowed = true;
							dbContext.SaveChanges();
							break;
						case "remove":
							commandOptions.Allowed = false;
							dbContext.SaveChanges();
							break;
						default:
							responseString = "Invalid parameters...\n" + e.Command.Description;
							break;
					}
				}

				dbContext.Dispose();
				await e.SendReplySafe(responseString);
			};
			this.Commands.Add(newCommand.Id.ToLower(), newCommand);

// !cmdChannelBlacklist
			newCommand = new Command("cmdChannelBlacklist");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Block a command from certain channels.";
			newCommand.ManPage = new ManPage("<CommandId> <add|remove> <ChannelId>", "`<CommandId>` - The command for which to change the restriction settings.\n\n`<add|remove>` - Add or remove to/from the restriction list.\n\n`<ChannelId>` - Id or mention of the channel in which to deny the execution of the Command");
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin;
			newCommand.OnExecute += async e => {
				if( e.MessageArgs == null || e.MessageArgs.Length < 2 ||
				    (e.MessageArgs[1].ToLower() != "add" && e.MessageArgs[1].ToLower() != "remove") ||
				    !guid.TryParse(e.MessageArgs[2].Trim('<', '#', '>'), out guid channelId) || e.Server.Guild.GetChannel(channelId) == null )
				{
					await e.SendReplySafe("Invalid parameters...\n" + e.Command.Description);
					return;
				}

				string commandId = e.Server.GetCommandOptionsId(e.MessageArgs[0]);
				if( commandId == null )
				{
					await e.SendReplySafe("I'm sorry but you can not restrict this command.");
					return;
				}
				if( commandId == "" )
				{
					await e.SendReplySafe($"Command `{e.MessageArgs[0]}` not found.");
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
				CommandChannelOptions commandOptions = dbContext.GetOrAddCommandChannelOptions(e.Server.Id, channelId, commandId);

				string responseString = "Success! \\o/";
				switch(e.MessageArgs[1].ToLower())
				{
					case "add":
						commandOptions.Blocked = true;
						dbContext.SaveChanges();
						break;
					case "remove":
						commandOptions.Blocked = false;
						dbContext.SaveChanges();
						break;
					default:
						responseString = "Invalid parameters...\n" + e.Command.Description;
						break;
				}

				dbContext.Dispose();
				await e.SendReplySafe(responseString);
			};
			this.Commands.Add(newCommand.Id.ToLower(), newCommand);

// !cmdChannelBlacklistAllCC
			newCommand = new Command("cmdChannelBlacklistAllCC");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Block all custom commands from certain channels.";
			newCommand.ManPage = new ManPage("<add|remove> <ChannelId>", "`<add|remove>` - Add or remove to/from the restriction list.\n\n`<ChannelId>` - Id or mention of the channel in which to deny the execution of the CustomCommands");
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin;
			newCommand.OnExecute += async e => {
				if( e.MessageArgs == null || e.MessageArgs.Length < 2 ||
				    (e.MessageArgs[0].ToLower() != "add" && e.MessageArgs[0].ToLower() != "remove") ||
				    !guid.TryParse(e.MessageArgs[1].Trim('<', '#', '>'), out guid channelId) || e.Server.Guild.GetChannel(channelId) == null )
				{
					await e.SendReplySafe("Invalid parameters...\n" + e.Command.Description);
					return;
				}

				string responseString = "Success! \\o/";
				ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
				foreach( CustomCommand cmd in e.Server.CustomCommands.Values )
				{
					CommandChannelOptions commandOptions = dbContext.GetOrAddCommandChannelOptions(e.Server.Id, channelId, cmd.CommandId);

					switch( e.MessageArgs[0].ToLower() )
					{
						case "add":
							commandOptions.Blocked = true;
							dbContext.SaveChanges();
							break;
						case "remove":
							commandOptions.Blocked = false;
							dbContext.SaveChanges();
							break;
						default:
							responseString = "Invalid parameters...\n" + e.Command.Description;
							break;
					}
				}

				dbContext.Dispose();
				await e.SendReplySafe(responseString);
			};
			this.Commands.Add(newCommand.Id.ToLower(), newCommand);

// !cmdResetRestrictions
			newCommand = new Command("cmdResetRestrictions");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Reset restrictions placed on a command by the _cmdChannelWhitelist_ and _cmdChannelBlacklist_ commands. Use with the `CommandID` as parameter.";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin;
			newCommand.OnExecute += async e => {
				if( e.MessageArgs == null || e.MessageArgs.Length < 1 )
				{
					await e.SendReplySafe("Invalid parameters...\n" + e.Command.Description);
					return;
				}

				string commandId = e.Server.GetCommandOptionsId(e.MessageArgs[0]);
				if( string.IsNullOrEmpty(commandId) )
				{
					await e.SendReplySafe($"Command `{e.MessageArgs[0]}` not found.");
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
				await dbContext.CommandChannelOptions.AsQueryable().Where(c => c.ServerId == e.Server.Id && c.CommandId == commandId)
					.ForEachAsync(c => c.Blacklisted = c.Whitelisted = false);

				dbContext.SaveChanges();
				dbContext.Dispose();

				await e.SendReplySafe("As you wish my thane.");
			};
			this.Commands.Add(newCommand.Id.ToLower(), newCommand);

// !cmdResetRestrictionsAllCC
			newCommand = new Command("cmdResetRestrictionsAllCC");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Reset restrictions placed on all custom commands by the _cmdChannelWhitelist_ and _cmdChannelBlacklist_ commands.";
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin;
			newCommand.OnExecute += async e => {
				ServerContext dbContext = ServerContext.Create(this.DbConnectionString);
				foreach( CustomCommand cmd in e.Server.CustomCommands.Values )
				{
					await dbContext.CommandChannelOptions.AsQueryable().Where(c => c.ServerId == e.Server.Id && c.CommandId == cmd.CommandId)
						.ForEachAsync(c => c.Blacklisted = c.Whitelisted = false);
				}

				dbContext.SaveChanges();
				dbContext.Dispose();

				await e.SendReplySafe("As you wish my thane.");
			};
			this.Commands.Add(newCommand.Id.ToLower(), newCommand);*/

/*
// !command
			newCommand = new Command("command");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "";
			newCommand.ManPage = new ManPage("", "");
			newCommand.RequiredPermissions = PermissionType.OwnerOnly;
			newCommand.OnExecute += async e => {
				string responseString = "";
				await e.SendReplySafe(responseString);
			};
			this.Commands.Add(newCommand.Id.ToLower(), newCommand);

*/

			return Task.CompletedTask;
		}
	}
}
