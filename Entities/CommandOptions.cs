using System;
using System.Collections.Generic;

using guid = System.UInt64;

namespace Valkyrja.entities
{
	public class CommandOptions
	{
		public guid ServerId{ get; set; } = 0;

		public string CommandId{ get; set; } = "";

		public PermissionOverrides PermissionOverrides{ get; set; } = PermissionOverrides.Default;

		public bool DeleteRequest{ get; set; } = false;

		public bool DeleteReply{ get; set; } = false;
	}

	public class CommandChannelOptions
	{
		public guid ServerId{ get; set; } = 0;

		public string CommandId{ get; set; } = "";

		public guid ChannelId{ get; set; } = 0;

		public bool Blocked{ get; set; } = false;

		public bool Allowed{ get; set; } = false;
	}
}
