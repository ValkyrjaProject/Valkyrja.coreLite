using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using Valkyrja.entities;
using Discord.Net;
using Discord.WebSocket;

using guid = System.UInt64;

namespace Valkyrja.coreLite
{
	public partial class ValkyrjaClient<T>: IValkyrjaClient, IDisposable where T: Config, new()
	{
		public async Task SendRawMessageToChannel(SocketTextChannel channel, string message)
		{
			//await LogMessage(LogType.Response, channel, this.GlobalConfig.UserId, message);
			await channel.SendMessageSafe(message);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool IsAdmin(guid id)
		{
			return this.Config.OwnerUserId == id;
		}

		public async Task LogException(Exception exception, CommandArguments args) =>
			await LogException(exception, "--Command: "+ args.Command.Id + " | Parameters: " + args.TrimmedMessage, args.Server.Id);

		public async Task LogException(Exception exception, string data, guid serverId = 0)
		{
			if( (exception is HttpException httpException && (int)httpException.HttpCode >= 500) || data.Contains("Error handling Dispatch") )
			{
				this.Monitoring?.Error500s.Inc();
			}

			if( (exception is WebSocketClosedException websocketException) )
			{
				data += $"\nCloseCode:{websocketException.CloseCode}\nReason:{websocketException.Reason}\nTarget:{websocketException.TargetSite}";
			}

			if( exception.Message == "Server requested a reconnect" ||
			    exception.Message == "Server missed last heartbeat" ||
			    exception.Message.Contains("Discord.PermissionTarget") ) //it's a spam
				return;

			Console.WriteLine(exception.Message);
			Console.WriteLine(exception.StackTrace);
			Console.WriteLine($"{data} | ServerId:{serverId}");

			if( exception is RateLimitedException || exception.Message.Contains("WebSocket connection was closed") ) //hack to not spam my logs
				return;

			if( exception.InnerException != null && exception.Message != exception.InnerException.Message )
				await LogException(exception.InnerException, "InnerException | " + data, serverId);
		}

		public List<SocketGuildUser> GetMentionedGuildUsers(CommandArguments e) //todo - Move this elsewhere...
		{
			List<SocketGuildUser> mentionedUsers = new List<SocketGuildUser>();
			foreach( SocketUser user in GetMentionedUsers(e) )
			{
				if(user is SocketGuildUser guildUser)
					mentionedUsers.Add(guildUser);
			}

			return mentionedUsers;
		}
		public List<SocketUser> GetMentionedUsers(CommandArguments e) //todo - Move this elsewhere...
		{
			List<SocketUser> mentionedUsers = new List<SocketUser>();

			if( e.Message.MentionedUsers != null && e.Message.MentionedUsers.Any() )
			{
				mentionedUsers.AddRange(e.Message.MentionedUsers);
			}
			else if( e.MessageArgs != null && e.MessageArgs.Length > 0 )
			{
				for( int i = 0; i < e.MessageArgs.Length; i++)
				{
					guid id;
					SocketUser user;
					if( !guid.TryParse(e.MessageArgs[i], out id) || id == 0 || (user = e.Server.Guild.GetUser(id)) == null )
						break;
					if( mentionedUsers.Contains(user) )
					{
						List<string> newArgs = new List<string>(e.MessageArgs);
						newArgs.RemoveAt(i);
						e.MessageArgs = newArgs.ToArray();
						continue;
					}

					mentionedUsers.Add(user);
				}
			}

			return mentionedUsers;
		}

		public List<guid> GetMentionedUserIds(CommandArguments e, bool endOnFailure = true) //todo - Move this elsewhere...
		{
			List<guid> mentionedIds = new List<guid>();

			/*if( e.Message.MentionedUsers != null && e.Message.MentionedUsers.Any() )
			{
				mentionedIds.AddRange(e.Message.MentionedUsers.Select(u => u.Id));
			}
			else*/ if( e.MessageArgs != null && e.MessageArgs.Length > 0 )
			{
				for( int i = 0; i < e.MessageArgs.Length; i++)
				{
					guid id;
					if( !guid.TryParse(e.MessageArgs[i].Trim('<','@','!','>'), out id) || id < int.MaxValue )
						if( endOnFailure ) break;
						else continue;
					if( mentionedIds.Contains(id) )
					{
						//This code is necessary to be able to further parse arguments by some commands (e.g. ban reason)
						List<string> newArgs = new List<string>(e.MessageArgs);
						newArgs.RemoveAt(i--);
						e.MessageArgs = newArgs.ToArray();
						continue;
					}

					mentionedIds.Add(id);
				}
			}

			return mentionedIds;
		}

		private string GetPatchnotes()
		{
			if( !Directory.Exists("updates") || !File.Exists(Path.Combine("updates", "changelog")) )
				return "This is not the original <https://valkyrja.app>, therefor I can not tell you, what's new here :<";

			string changelog = File.ReadAllText(Path.Combine("updates", "changelog"));
			int start = changelog.IndexOf("**Valkyrja");
			int valkEnd = changelog.Substring(start+1).IndexOf("**Valkyrja") + 1;
			int bwEnd = changelog.Substring(start+1).IndexOf("**Valkyrja") + 1;
			int end = valkEnd > start ? valkEnd : bwEnd;
			int hLength = valkEnd > start ? "**Valkyrja".Length : "**Valkyrja".Length;

			if( start >= 0 && end <= changelog.Length && end > start && (changelog = changelog.Substring(start, end-start+hLength)).Length > 0 )
				return changelog + "\n\nSee the full changelog and upcoming features at <https://valkyrja.app/updates>!";

			return "There is an error in the data so I have failed to retrieve the patchnotes. Sorry mastah!";
		}

		/// <summary>
		/// Returns:
		///  1 = success;
		///  0 = first 3 attempts failed;
		/// -1 = more than 3 attempts failed;
		/// -2 = failed due to Discord server issues;
		/// -3 = user not found;
		/// </summary>
		public async Task<int> SendPmSafe(SocketUser user, string message)
		{
			if( user == null )
				return -3;
			if( this.FailedPmCount.ContainsKey(user.Id) && this.FailedPmCount[user.Id] >= 3 )
				return -1;
			try
			{
				await user.SendMessageSafe(message);
				return 1;
			}
			catch( HttpException e ) when( (int)e.HttpCode == 403 || (e.DiscordCode.HasValue && e.DiscordCode == 50007) || e.Message.Contains("50007") )
			{
				if( !this.FailedPmCount.ContainsKey(user.Id) )
					this.FailedPmCount.Add(user.Id, 0);
				this.FailedPmCount[user.Id]++;
				return 0;
			}
			catch( HttpException e ) when( (int)e.HttpCode >= 500 )
			{
				this.Monitoring?.Error500s.Inc();
				return -2;
			}
			catch( Exception e )
			{
				await LogException(e, "Unknown PM error.", 0);
				return -2;
			}
		}
	}
}
