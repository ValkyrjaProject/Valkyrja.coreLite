using System;
using Prometheus;

namespace Valkyrja.entities
{
	public class Monitoring: IDisposable
	{
		private readonly MetricPusher Prometheus;

		public readonly Counter Disconnects = Metrics.CreateCounter("discord_valk_dc", "Valkyrja: disconnects");
		public readonly Counter Error500s = Metrics.CreateCounter("discord_valk_500", "Valkyrja: Discord server error 500s");
		public readonly Counter Messages = Metrics.CreateCounter("discord_valk_msg", "Valkyrja: Messages received");
		public readonly Counter Commands = Metrics.CreateCounter("discord_valk_cmd", "Valkyrja: Commands executed");
		public readonly Gauge Uptime = Metrics.CreateGauge("discord_valk_uptime", "Valkyrja: Uptime in seconds");

		private Monitoring(Config config, int shardId)
		{
			string instance = shardId.ToString();
			if( !string.IsNullOrEmpty(config.PrometheusInstance) )
				instance = config.PrometheusInstance + "_" + shardId.ToString();

			if( this.Prometheus == null )
				this.Prometheus = new MetricPusher(config.PrometheusEndpoint, config.PrometheusJob, instance, config.PrometheusInterval);
			this.Prometheus.Start();
		}

		public static Monitoring Create(Config config, int shardId)
		{
			if( string.IsNullOrEmpty(config.PrometheusEndpoint) || string.IsNullOrEmpty(config.PrometheusJob) )
				return null;
			return new Monitoring(config, shardId);
		}

		public void Dispose()
		{
			this.Prometheus.Stop();
			((IDisposable)this.Prometheus)?.Dispose();
		}
	}
}
