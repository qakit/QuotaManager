namespace GridQuota;

using System;
using System.Collections.Generic;

public class HostConfig
{
	public string HostUri { get; set; } = "";
	public int Limit { get; set; }
}

public class AppConfig
{
	public List<HostConfig> Hosts { get; } = [];

	// public TimeSpan Timeout { get; set; } = new TimeSpan(0, 0, 60);
	// public TimeSpan MaxTimeout { get; set; } = new TimeSpan(1, 0, 0);

	public TimeSpan CheckAliveInterval { get; set; } = new TimeSpan(0, 0, 10);

	public int SessionRetryCount { get; set; } = 3;
	public TimeSpan SessionRetryTimeout { get; set; } = new TimeSpan(0, 0, 30);

}
