using System;
using System.Linq;
using System.Collections.Generic;
using Discord.WebSocket;

using guid = System.UInt64;

namespace Valkyrja.entities
{
	public class CustomCommand
	{
		public guid ServerId{ get; set; } = 0;

		public string CommandId{ get; set; } = "";

		public string Response{ get; set; } = "This custom command was not configured.";

		public string Description{ get; set; } = "This is custom command on this server.";

		public bool MentionsEnabled{ get; set; } = false;

		/// <summary> Returns true if the User has permission to execute this command. </summary>
		/// <param name="commandChannelOptions"> List of all the channel options for specific command. </param>
		public bool CanExecute(IValkyrjaClient client, Server server, SocketGuildChannel channel,
			SocketGuildUser user)
		{
			if( client.IsAdmin(user.Id) )
				return true;

			return server.CanExecuteCommand(this.CommandId, PermissionType.Everyone, channel, user);
		}
	}

	public class CustomAlias
	{
		public guid ServerId{ get; set; } = 0;

		public string CommandId{ get; set; } = "";

		public string Alias{ get; set; } = "";
	}
}
