using System;
using System.Threading.Tasks;
using System.IO;
using Newtonsoft.Json;

using guid = System.UInt64;

namespace Valkyrja.entities
{
	public class Config
	{
		public const int MessageCharacterLimit = 2000;
		public const int EmbedValueCharacterLimit = 1000;
		public const string Filename = "config.json";
		private Object _Lock = new Object();

		private string Path = "config.json";

		public bool Debug = false;
		public bool IsValkyrjaHosted = false;
		public string DiscordToken = null;
		public string GameStatus = "at https://valkyrja.app";
		public string CommandPrefix = "!";
		public bool ExecuteOnEdit = true;
		public bool IgnoreBots = true;
		public bool IgnoreEveryone = true;
		public guid OwnerUserId = 0;
		public guid NotificationChannelId = 0;
		public guid[] AdminRoleIds = {0};
		public guid[] ModeratorRoleIds = {0};
		public guid[] SubModeratorRoleIds = {0};
		public bool DownloadUsers = true;
		public int MessageCacheSize = 100;
		public int TotalShards = 1;
		public int InitialUpdateDelay = 180;
		public int OperationsMax = 2;
		public int OperationsExtra = 1;
		public float TargetFps = 0.05f;
		public string PrometheusEndpoint = "";
		public string PrometheusJob = "";
		public string PrometheusInstance = "";
		public long PrometheusInterval = 5000;

		public static T Load<T>(string path = null) where T: Config, new()
		{
			if( !string.IsNullOrEmpty(path) )
				path = System.IO.Path.Combine(path, Filename);
			else
				path = Filename;

			if( !File.Exists(path) )
			{
				string json = JsonConvert.SerializeObject(new T(){Path = path}, Formatting.Indented);
				File.WriteAllText(path, json);
			}

			T newConfig = JsonConvert.DeserializeObject<T>(File.ReadAllText(path));

			return newConfig;
		}

		public void SaveAsync()
		{
			Task.Run(() => Save());
		}
		private void Save()
		{
			lock(this._Lock)
			{
				string json = JsonConvert.SerializeObject(this, Formatting.Indented);
				File.WriteAllText(this.Path, json);
			}
		}
	}
}
