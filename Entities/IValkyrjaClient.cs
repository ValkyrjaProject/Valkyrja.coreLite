using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

using guid = System.UInt64;

namespace Valkyrja.entities
{
	public interface IValkyrjaClient
	{
		bool IsConnected{ get; set; }
		BaseConfig CoreConfig{ get; set; }
		Monitoring Monitoring{ get; set; }
		List<Operation> CurrentOperations{ get; set; }
		Object OperationsLock{ get; set; }
		int OperationsRan{ get; set; }
		ConcurrentDictionary<guid, Server> Servers{ get; set; }

		bool IsAdmin(guid id);
		Task LogException(Exception exception, CommandArguments args);
		Task LogException(Exception exception, string data, guid serverId = 0);
		Task<int> SendPmSafe(SocketUser user, string message, Embed embed = null);
	}
}
