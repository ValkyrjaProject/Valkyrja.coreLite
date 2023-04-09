using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using Discord;
using Valkyrja.entities;
using Discord.Net;
using Discord.WebSocket;

using guid = System.UInt64;

namespace Valkyrja.coreLite
{
	public partial class ValkyrjaClient<T>: IValkyrjaClient, IDisposable where T: BaseConfig, new()
	{
		public async Task SendRawMessageToChannel(SocketTextChannel channel, string message)
		{
			//await LogMessage(LogType.Response, channel, this.GlobalConfig.UserId, message);
			await channel.SendMessageSafe(message);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool IsAdmin(guid id)
		{
			return this.CoreConfig.OwnerUserId == id;
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

			Console.WriteLine($"{exception.GetType()}: {exception.Message}");
			Console.WriteLine(exception.StackTrace);
			Console.WriteLine($"{data} | ServerId:{serverId}");

			if( exception is RateLimitedException || exception.Message.Contains("WebSocket connection was closed") ) //hack to not spam my logs
				return;

			if( exception.InnerException != null && exception.Message != exception.InnerException.Message )
				await LogException(exception.InnerException, "InnerException | " + data, serverId);
		}

		public async Task<List<IGuildUser>> GetMentionedGuildUsers(CommandArguments e) //todo - Move this elsewhere...
		{

			if( e.MessageArgs == null || e.MessageArgs.Length == 0 )
				return new List<IGuildUser>();

			List<IGuildUser> mentionedUsers = new List<IGuildUser>();
			for( int i = 0; i < e.MessageArgs.Length; i++ )
			{
				guid id;
				if( !guid.TryParse(e.MessageArgs[i].Trim('<','@','!','>'), out id) || id == 0 )
					break;

				IGuildUser user = e.Server.Guild.GetUser(id);
				if( user == null )
					user = await this.DiscordClient.Rest.GetGuildUserAsync(e.Server.Id, id);
				if( user == null )
					continue;

				if( mentionedUsers.Contains(user) )
				{
					List<string> newArgs = new List<string>(e.MessageArgs);
					newArgs.RemoveAt(i);
					e.MessageArgs = newArgs.ToArray();
					continue;
				}

				mentionedUsers.Add(user);
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
		public async Task<int> SendPmSafe(SocketUser user, string message, Embed embed = null)
		{
			if( user == null )
				return -3;
			if( this.FailedPmCount.ContainsKey(user.Id) && this.FailedPmCount[user.Id] >= 3 )
				return -1;
			try
			{
				await user.SendMessageSafe(message, embed);
				return 1;
			}
			catch( HttpException e ) when( (int)e.HttpCode == 403 || (e.DiscordCode.HasValue && e.DiscordCode == DiscordErrorCode.CannotSendMessageToUser) || e.Message.Contains("50007") )
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


		private string GetStatusString(TimeSpan latency, Server server)
		{
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
				           $"[ Discord Latency ][ {latency.TotalMilliseconds:#00}                     ]\n```";
			}

			return message;
		}
	}
}
