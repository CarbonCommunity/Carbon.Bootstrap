﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using API.Abstracts;
using API.Analytics;
using Facepunch.Rust;
using HarmonyLib;
using Newtonsoft.Json;
using Utility;

/*
 *
 * Copyright (c) 2022-2024 Carbon Community
 * All rights reserved.
 *
 */

namespace Components;
#pragma warning disable IDE0051

internal sealed class AnalyticsManager : CarbonBehaviour, IAnalyticsManager
{
	private int _sessions;
	private float _lastUpdate;
	private float _lastEngagement;
	private static string _location;

	private const string MeasurementID = "G-M7ZBRYS3X7";
	private const string MeasurementSecret = "edBQH3_wRCWxZSzx5Y2IWA";

	public bool HasNewIdentifier
	{ get; private set; }

	public string Branch
	{ get => _branch.Value; }

	private static readonly Lazy<string> _branch = new(() =>
	{
		return _infoVersion.Value switch
		{
			string s when s.Contains("Debug") => "debug",
			string s when s.Contains("Release") => "release",
			string s when s.Contains("Minimal") => "minimal",
			_ => "Unknown"
		};
	});

	public string InformationalVersion
	{ get => _infoVersion.Value; }

	private static readonly Lazy<string> _infoVersion = new(() =>
	{
		return AccessTools.TypeByName("Carbon.Community")
			.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;
	});

	public string Platform
	{ get => _platform.Value; }

	public bool IsMinimalBuild =>
#if MINIMAL
		true;
#else
		false;
#endif

	private static readonly Lazy<string> _platform = new(() =>
	{
		return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) switch
		{
			true => "windows",
			false => "linux"
		};
	});

	public string Protocol
	{ get => _protocol.Value; }

	private static readonly Lazy<string> _protocol = new(() =>
	{
		return AccessTools.TypeByName("Carbon.Community")
			.Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>().Version;
	});

	public string UserAgent
	{ get => _userAgent.Value; }

	private static readonly Lazy<string> _userAgent = new(() =>
	{
		return $"carbon/{_version.Value} ({_platform.Value}; x64; {_branch.Value};"
			+ $" +https://github.com/CarbonCommunity/Carbon.Core)";
	});

	public string Version
	{ get => _version.Value; }

	private static readonly Lazy<string> _version = new(() =>
	{
		return AccessTools.TypeByName("Carbon.Community")
			.Assembly.GetName().Version.ToString();
	});

	public string ClientID
	{ get => _serverInfo.Value.UID; }

	private static readonly Lazy<Identity> _serverInfo = new(() =>
	{
		Identity info;

		try
		{
			_location = Path.Combine(Context.Game, "server", ConVar.Server.identity, "carbon.id");

			if (File.Exists(_location))
			{
				string raw = File.ReadAllText(_location);
				info = JsonConvert.DeserializeObject<Identity>(raw);
				if (!_serverInfo.Equals(default(Identity))) return info;
			}

			Carbon.Bootstrap.Analytics.HasNewIdentifier = true;
			info = new Identity { UID = $"{Guid.NewGuid()}" };
			Logger.Warn($"A new server identity was generated.");
			File.WriteAllText(_location, JsonConvert.SerializeObject(info, Formatting.Indented));
		}
		catch (Exception e)
		{
			Logger.Error("Unable to process server identity", e);
		}

		return info;
	});

	public string SessionID
	{ get; private set; }

	public string SystemID
	{ get => UnityEngine.SystemInfo.deviceUniqueIdentifier; }

	public Dictionary<string, object> Segments { get; set; }

	public void Awake()
	{
		HasNewIdentifier = false;
		_lastUpdate = 0;
		_lastEngagement = float.MinValue;
		SessionID = Util.GetRandomNumber(10);

		if (File.Exists(Path.Combine(Context.Carbon, ".nostats")))
		{
			Logger.Warn("You have opted out from analytics data collection");
			enabled = false;
		}
		else
		{
			Logger.Warn("We use Google Analytics to collect basic data about Carbon such as"
				+ " Carbon version, platform, branch and plug-in count.");
			Logger.Warn("We have no access to any personal identifiable data such as"
				+ " steamids, server name, ip:port, title or description.");
			Logger.Warn("If you'd like to opt-out create an empty '.nostats' file at the Carbon root folder.");
		}

		Segments = new Dictionary<string, object> {
			{ "branch", Branch },
			{ "platform", Platform },
		};
	}

	private void Update()
	{
		_lastUpdate += UnityEngine.Time.deltaTime;
		if (_lastUpdate < 300) return;

		_lastUpdate = 0;
		LogEvent("user_engagement");
	}

	public void SessionStart()
		=> LogEvent(HasNewIdentifier ? "first_visit" : "user_engagement");

	public void LogEvent(string eventName)
		=> SendEvent(eventName);

	public void LogEvent(string eventName, IDictionary<string, object> segments = null, IDictionary<string, object> metrics = null)
		=> SendMPEvent(eventName, segments, metrics);

	public void SendEvent(string eventName)
	{
		if (!enabled) return;

		float delta = Math.Min(Math.Max(
			UnityEngine.Time.realtimeSinceStartup - _lastEngagement, 0f), float.MaxValue);
		_lastEngagement = UnityEngine.Time.realtimeSinceStartup;

		string url = "https://www.google-analytics.com/g/collect";
		string query = $"v=2&tid={MeasurementID}&cid={ClientID}&en={eventName}";

		if (delta >= 1800f)
		{
			if (delta == float.MaxValue) delta = 0;
			SessionID = Util.GetRandomNumber(10);
			query += $"&_ss=1";
			_sessions++;
		}

		query += $"&seg=1&_et={Math.Round(delta * 1000f)}&sid={SessionID}&sct={_sessions}";

#if DEBUG_VERBOSE
		query += "&_dbg=1";
#endif

		SendRequest($"{url}?{query}&_z={Util.GetRandomNumber(10)}");
	}

	private void SendMPEvent(string eventName, IDictionary<string, object> segments, IDictionary<string, object> metrics)
	{
		if (!enabled) return;

		float delta = Math.Min(Math.Max(
			UnityEngine.Time.realtimeSinceStartup - _lastEngagement, 0f), 1800f);
		_lastEngagement = UnityEngine.Time.realtimeSinceStartup;

		string url = "https://www.google-analytics.com/mp/collect";
		string query = $"api_secret={MeasurementSecret}&measurement_id={MeasurementID}";
		Dictionary<string, object> user_properties = new();

		Dictionary<string, object> event_parameters = new() {
#if DEBUG_VERBOSE
			{ "debug_mode", 1 },
#endif
			{ "session_id", SessionID },
			{ "engagement_time_msec", Math.Round(delta * 1000f) },
		};

		Dictionary<string, object> body = new Dictionary<string, object>
		{
			{ "client_id", ClientID },
			{ "non_personalized_ads", true },
		};

		if (metrics != null)
		{
			foreach (var metric in metrics)
				event_parameters.Add(metric.Key, metric.Value);
		}

		List<Dictionary<string, object>> @events = new List<Dictionary<string, object>> {
			new Dictionary<string, object> {
				{ "name", eventName },
				{ "params", event_parameters }
			}
		};

		body.Add("events", value: @events);

		if (segments != null)
		{
			foreach (var segment in segments)
			{
				user_properties.Add(segment.Key, new Dictionary<string, object> {
					{"value", segment.Value}
				});
			}
			body.Add("user_properties", user_properties);
		}

		SendRequest($"{url}?{query}", JsonConvert.SerializeObject(body));

		metrics?.Clear();
		user_properties.Clear();
		event_parameters.Clear();
		@events.Clear();
		body.Clear();

		metrics = null;
		user_properties = null;
		event_parameters = null;
		@events = null;
		body = null;
	}

	private void SendRequest(string url, string body = null)
	{
		try
		{
			body ??= string.Empty;

			using WebClient webClient = new WebClient();
			webClient.Headers.Add(HttpRequestHeader.UserAgent, UserAgent);
			webClient.Headers.Add(HttpRequestHeader.ContentType, "application/json");
			webClient.UploadStringCompleted += UploadStringCompleted;
			webClient.UploadStringAsync(new Uri(url), "POST", body, url);

#if DEBUG_VERBOSE
			Logger.Debug($"Request sent to Google Analytics");
			Logger.Debug($" > {url}");
#endif
		}
#if DEBUG_VERBOSE
		catch (System.Exception e)
		{
			Logger.Warn($"Failed to send request to Google Analytics ({e.Message})");
			Logger.Debug($" > {url}");
		}
#else
		catch (System.Exception) { }
#endif
	}

	private void UploadStringCompleted(object sender, UploadStringCompletedEventArgs e)
	{
		WebClient webClient = (WebClient)sender;
		string url = (string)e.UserState;

		try
		{
			if (e.Error != null) throw new Exception(e.Error.Message);
			if (e.Cancelled) throw new Exception("Job was cancelled");
		}
#if DEBUG_VERBOSE
		catch (System.Exception ex)
		{
			Logger.Warn($"Failed to send request to Google Analytics ({ex.Message})");
			Logger.Debug($" > {url}");
		}
#else
		catch (System.Exception) { }
#endif
		finally
		{
			webClient.Dispose();
		}
	}
}
