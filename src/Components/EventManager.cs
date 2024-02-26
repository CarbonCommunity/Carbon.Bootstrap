﻿using System;
using System.Collections.Generic;
using API.Abstracts;
using API.Events;
using Utility;

/*
 *
 * Copyright (c) 2022-2024 Carbon Community
 * All rights reserved.
 *
 */

namespace Components;

#pragma warning disable UNT0006

internal sealed class EventManager : CarbonBehaviour, IEventManager
{
	private readonly Dictionary<CarbonEvent, Delegate> events = new();

	public void Subscribe(CarbonEvent eventId, Action<EventArgs> callback)
	{
		if (!events.ContainsKey(eventId)) events[eventId] = callback;
		else events[eventId] = Delegate.Combine(events[eventId], callback);
		Utility.Logger.Debug($"[{eventId}] New subscriptor '{callback.Target}' ('{callback.Method}')");
	}

	public void Trigger(CarbonEvent eventId, EventArgs args)
	{
#if DEBUG
		CarbonEventArgs parsed = args as CarbonEventArgs;
		string payload = (args == EventArgs.Empty) ? "empty payload" : $"{parsed.Payload}";
		Utility.Logger.Debug($"[{eventId}] {payload}");
#endif
		if (!events.ContainsKey(eventId)) return;
		Action<EventArgs> @event = events[eventId] as Action<EventArgs>;

		try
		{
			@event?.Invoke(args);
		}
		catch(Exception ex)
		{
			Logger.Error($"Failed executing {eventId}", ex);
		}
	}

	public void Unsubscribe(CarbonEvent eventId, Action<EventArgs> callback)
	{
		if (!events.ContainsKey(eventId)) return;
		events[eventId] = Delegate.Remove(events[eventId], callback);
		Utility.Logger.Debug($"[{eventId}] Remove subscription '{callback.Target}'");
	}

	public void Reset(CarbonEvent eventId)
	{
		if (!events.ContainsKey(eventId)) return;

		events[eventId] = null;
		events.Remove(eventId);
	}
}
