namespace GridQuota;

using System;
using System.Collections.Generic;

public class HostConfig
{
	public string HostUri { get; set; } = "";
	public int Limit { get; set; }
}

public class UserConfig
{
	public string Name { get; set; } = "";
	public string Secret { get; set; } = "";
	public int MaxSessions { get; set; } = 20;
}

public class AppConfig
{
	public List<HostConfig> Hosts { get; } = [];
	public List<UserConfig> Users { get; } = [];

	/// <summary>
	/// Timeout for stopping host (grid).
	/// </summary>
	public TimeSpan StopHostTimeout { get; set; } = new TimeSpan(0, 0, 3);

	/// <summary>
	/// How much time do we let session to be alive after connection. It used to be sliding expiration
	/// since last access but with WebDriver v4 which uses websockets to communicate with the browser this is not possible.
	/// </summary>
	public TimeSpan SessionTimeout { get; set; } = new TimeSpan(0, 2, 0);

	// public TimeSpan MaxTimeout { get; set; } = new TimeSpan(1, 0, 0);

	public TimeSpan CheckAliveInterval { get; set; } = new TimeSpan(0, 0, 10);

	public int SessionRetryCount { get; set; } = 3;
	public TimeSpan SessionRetryTimeout { get; set; } = new TimeSpan(0, 0, 30);

}
