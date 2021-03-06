﻿using UnityEngine;
using System;
using System.Threading.Collections;
using System.Collections;
using System.Collections.Generic;

namespace DDP {

	/*
	 * DDP protocol:
	 *   https://github.com/meteor/meteor/blob/master/packages/ddp/DDP.md
	 */
	public class DdpConnection : IDisposable {

		// The possible values for the "msg" field.
		public class MessageType {
			// Client -> server.
			public const string CONNECT = "connect";
			public const string PONG    = "pong";
			public const string SUB     = "sub";
			public const string UNSUB   = "unsub";
			public const string METHOD  = "method";

			// Server -> client.
			public const string CONNECTED    = "connected";
			public const string FAILED       = "failed";
			public const string PING         = "ping";
			public const string NOSUB        = "nosub";
			public const string ADDED        = "added";
			public const string CHANGED      = "changed";
			public const string REMOVED      = "removed";
			public const string READY        = "ready";
			public const string ADDED_BEFORE = "addedBefore";
			public const string MOVED_BEFORE = "movedBefore";
			public const string RESULT       = "result";
			public const string UPDATED      = "updated";
			public const string ERROR        = "error";
		}

		// Field names supported in the DDP protocol.
		public class Field {
			public const string SERVER_ID   = "server_id";
			public const string MSG         = "msg";
			public const string SESSION     = "session";
			public const string VERSION     = "version";
			public const string SUPPORT     = "support";

			public const string NAME        = "name";
			public const string PARAMS      = "params";
			public const string SUBS        = "subs";
			public const string COLLECTION  = "collection";
			public const string FIELDS      = "fields";
			public const string CLEARED     = "cleared";
			public const string BEFORE      = "before";

			public const string ID          = "id";
			public const string METHOD      = "method";
			public const string METHODS     = "methods";
			public const string RANDOM_SEED = "randomSeed"; // unused
			public const string RESULT      = "result";

			public const string ERROR       = "error";
			public const string REASON      = "reason";
			public const string DETAILS     = "details";
			public const string MESSAGE     = "message";   // undocumented
			public const string ERROR_TYPE  = "errorType"; // undocumented
			public const string OFFENDING_MESSAGE = "offendingMessage";
		}

		public enum ConnectionState {
			NOT_CONNECTED,
			DISCONNECTED,
			CONNECTED,
			CLOSED
		}

		// The DDP protocol version implemented by this library.
		public const string DDP_PROTOCOL_VERSION = "1";

		private CoroutineHelper coroutineHelper;
		private ConcurrentQueue<JSONObject> messageQueue = new ConcurrentQueue<JSONObject>();

		private WebSocketSharp.WebSocket ws;
		private ConnectionState ddpConnectionState;
		private string sessionId;

		private Dictionary<string, Subscription> subscriptions = new Dictionary<string, Subscription>();
		private Dictionary<string, MethodCall> methodCalls = new Dictionary<string, MethodCall>();

		private int subscriptionId;
		private int methodCallId;

		public delegate void OnConnectedDelegate(DdpConnection connection);
		public delegate void OnDisconnectedDelegate(DdpConnection connection);
		public delegate void OnAddedDelegate(string collection, string docId, JSONObject fields);
		public delegate void OnChangedDelegate(string collection, string docId, JSONObject fields, JSONObject cleared);
		public delegate void OnRemovedDelegate(string collection, string docId);
		public delegate void OnAddedBeforeDelegate(string collection, string docId, JSONObject fields, string before);
		public delegate void OnMovedBeforeDelegate(string collection, string docId, string before);
		public delegate void OnErrorDelegate(DdpError error);

		public event OnConnectedDelegate OnConnected;
		public event OnDisconnectedDelegate OnDisconnected;
		public event OnAddedDelegate OnAdded;
		public event OnChangedDelegate OnChanged;
		public event OnRemovedDelegate OnRemoved;
		public event OnAddedBeforeDelegate OnAddedBefore;
		public event OnMovedBeforeDelegate OnMovedBefore;
		public event OnErrorDelegate OnError;

		public bool logMessages;

		public DdpConnection(string url) {
			coroutineHelper = CoroutineHelper.GetInstance();
			coroutineHelper.StartCoroutine(HandleMessages());

			ws = new WebSocketSharp.WebSocket(url);
			ws.OnOpen += OnWebSocketOpen;
			ws.OnError += OnWebSocketError;
			ws.OnClose += OnWebSocketClose;
			ws.OnMessage += OnWebSocketMessage;
		}

		private void OnWebSocketOpen(object sender, EventArgs e) {
			Send(GetConnectMessage());

			foreach (Subscription subscription in subscriptions.Values) {
				Send(GetSubscriptionMessage(subscription));
			}
			foreach (MethodCall methodCall in methodCalls.Values) {
				Send(GetMethodCallMessage(methodCall));
			}
		}

		private void OnWebSocketError(object sender, WebSocketSharp.ErrorEventArgs e) {
			coroutineHelper.RunInMainThread(() => {
				if (OnError != null) {
					OnError(new DdpError() {
						errorCode = "WebSocket error",
						reason = e.Message
					});
				}
			});
		}

		private void OnWebSocketClose(object sender, WebSocketSharp.CloseEventArgs e) {
			if (e.WasClean) {
				ddpConnectionState = ConnectionState.CLOSED;
			}
			else {
				ddpConnectionState = ConnectionState.DISCONNECTED;
				coroutineHelper.RunInMainThread(() => {
					if (OnDisconnected != null) {
						OnDisconnected(this);
					}
				});
			}
		}

		private void OnWebSocketMessage(object sender, WebSocketSharp.MessageEventArgs e) {
			if (logMessages) Debug.Log("OnMessage: " + e.Data);
			JSONObject message = new JSONObject(e.Data);
			messageQueue.Enqueue(message);
		}

		private IEnumerator HandleMessages() {
			while (true) {
				JSONObject message = null;
				while (messageQueue.TryDequeue(out message)) {
					HandleMessage(message);
				}
				yield return null;
			}
		}

		private void HandleMessage(JSONObject message) {
			if (!message.HasField(Field.MSG)) {
				// Silently ignore those messages.
				return;
			}

			switch (message[Field.MSG].str) {
			case MessageType.CONNECTED: {
					sessionId = message[Field.SESSION].str;
					ddpConnectionState = ConnectionState.CONNECTED;

					if (OnConnected != null) {
						OnConnected(this);
					}
					break;
				}

			case MessageType.FAILED: {
					if (OnError != null) {
						OnError(new DdpError() {
							errorCode = "Connection refused",
							reason = "The server is using an unsupported DDP protocol version: " +
								message[Field.VERSION]
						});
					}
					Close();
					break;
				}

			case MessageType.PING: {
					if (message.HasField(Field.ID)) {
						Send(GetPongMessage(message[Field.ID].str));
					}
					else {
						Send(GetPongMessage());
					}
					break;
				}

			case MessageType.NOSUB: {
					string subscriptionId = message[Field.ID].str;
					subscriptions.Remove(subscriptionId);

					if (message.HasField(Field.ERROR)) {
						if (OnError != null) {
							OnError(GetError(message[Field.ERROR]));
						}
					}
					break;
				}

			case MessageType.ADDED: {
					if (OnAdded != null) {
						OnAdded(
							message[Field.COLLECTION].str,
							message[Field.ID].str,
							message[Field.FIELDS]);
					}
					break;
				}

			case MessageType.CHANGED: {
					if (OnChanged != null) {
						OnChanged(
							message[Field.COLLECTION].str,
							message[Field.ID].str,
							message[Field.FIELDS],
							message[Field.CLEARED]);
					}
					break;
				}

			case MessageType.REMOVED: {
					if (OnRemoved != null) {
						OnRemoved(
							message[Field.COLLECTION].str,
							message[Field.ID].str);
					}
					break;
				}

			case MessageType.READY: {
					string[] subscriptionIds = ToStringArray(message[Field.SUBS]);

					foreach (string subscriptionId in subscriptionIds) {
						Subscription subscription = subscriptions[subscriptionId];
						if (subscription != null) {
							subscription.isReady = true;
							if (subscription.OnReady != null) {
								subscription.OnReady(subscription);
							}
						}
					}
					break;
				}

			case MessageType.ADDED_BEFORE: {
					if (OnAddedBefore != null) {
						OnAddedBefore(
							message[Field.COLLECTION].str,
							message[Field.ID].str,
							message[Field.FIELDS],
							message[Field.BEFORE].str);
					}
					break;
				}

			case MessageType.MOVED_BEFORE: {
					if (OnMovedBefore != null) {
						OnMovedBefore(
							message[Field.COLLECTION].str,
							message[Field.ID].str,
							message[Field.BEFORE].str);
					}
					break;
				}

			case MessageType.RESULT: {
					string methodCallId = message[Field.ID].str;
					MethodCall methodCall = methodCalls[methodCallId];
					if (methodCall != null) {
						if (message.HasField(Field.ERROR)) {
							methodCall.error = GetError(message[Field.ERROR]);
						}
						methodCall.result = message[Field.RESULT];
						if (methodCall.hasUpdated) {
							methodCalls.Remove(methodCallId);
						}
						methodCall.hasResult = true;
						if (methodCall.OnResult != null) {
							methodCall.OnResult(methodCall);
						}
					}
					break;
				}

			case MessageType.UPDATED: {
					string[] methodCallIds = ToStringArray(message[Field.METHODS]);
					foreach (string methodCallId in methodCallIds) {
						MethodCall methodCall = methodCalls[methodCallId];
						if (methodCall != null) {
							if (methodCall.hasResult) {
								methodCalls.Remove(methodCallId);
							}
							methodCall.hasUpdated = true;
							if (methodCall.OnUpdated != null) {
								methodCall.OnUpdated(methodCall);
							}
						}
					}
					break;
				}

			case MessageType.ERROR: {
					if (OnError != null) {
						OnError(GetError(message));
					}
					break;
				}
			}
		}

		private string GetConnectMessage() {
			JSONObject message = new JSONObject(JSONObject.Type.OBJECT);
			message.AddField(Field.MSG, MessageType.CONNECT);
			if (sessionId != null) {
				message.AddField(Field.SESSION, sessionId);
			}
			message.AddField(Field.VERSION, DDP_PROTOCOL_VERSION);

			JSONObject supportedVersions = new JSONObject(JSONObject.Type.ARRAY);
			supportedVersions.Add(DDP_PROTOCOL_VERSION);
			message.AddField(Field.SUPPORT, supportedVersions);

			return message.Print();
		}

		private string GetPongMessage() {
			JSONObject message = new JSONObject(JSONObject.Type.OBJECT);
			message.AddField(Field.MSG, MessageType.PONG);

			return message.Print();
		}

		private string GetPongMessage(string id) {
			JSONObject message = new JSONObject(JSONObject.Type.OBJECT);
			message.AddField(Field.MSG, MessageType.PONG);
			message.AddField(Field.ID, id);

			return message.Print();
		}

		private string GetSubscriptionMessage(Subscription subscription) {
			JSONObject message = new JSONObject(JSONObject.Type.OBJECT);
			message.AddField(Field.MSG, MessageType.SUB);
			message.AddField(Field.ID, subscription.id);
			message.AddField(Field.NAME, subscription.name);
			if (subscription.items.Length > 0) {
				message.AddField(Field.PARAMS, new JSONObject(subscription.items));
			}

			return message.Print();
		}

		private string GetUnsubscriptionMessage(Subscription subscription) {
			JSONObject message = new JSONObject(JSONObject.Type.OBJECT);
			message.AddField(Field.MSG, MessageType.UNSUB);
			message.AddField(Field.ID, subscription.id);

			return message.Print();
		}

		private string GetMethodCallMessage(MethodCall methodCall) {
			JSONObject message = new JSONObject(JSONObject.Type.OBJECT);
			message.AddField(Field.MSG, MessageType.METHOD);
			message.AddField(Field.METHOD, methodCall.methodName);
			if (methodCall.items.Length > 0) {
				message.AddField(Field.PARAMS, new JSONObject(methodCall.items));
			}
			message.AddField(Field.ID, methodCall.id);
			//message.AddField(Field.RANDOM_SEED, xxx);

			return message.Print();
		}

		private DdpError GetError(JSONObject obj) {
			string errorCode = null;
			if (obj.HasField(Field.ERROR)) {
				JSONObject errorCodeObj = obj[Field.ERROR];
				errorCode = errorCodeObj.IsNumber ? "" + errorCodeObj.i : errorCodeObj.str;
			}

			return new DdpError() {
				errorCode = errorCode,
				reason = obj[Field.REASON].str,
				message = obj.HasField(Field.MESSAGE) ? obj[Field.MESSAGE].str : null,
				errorType = obj.HasField(Field.ERROR_TYPE) ? obj[Field.ERROR_TYPE].str : null,
				offendingMessage = obj.HasField(Field.OFFENDING_MESSAGE) ? obj[Field.OFFENDING_MESSAGE].str : null
			};
		}

		private string[] ToStringArray(JSONObject jo) {
			string[] result = new string[jo.Count];
			for (int i = 0; i < result.Length; i++) {
				result[i] = jo[i].str;
			}
			return result;
		}

		private void Send(string message) {
			if (logMessages) Debug.Log("Send: " + message);
			ws.Send(message);
		}

		public ConnectionState GetConnectionState() {
			return ddpConnectionState;
		}

		public void Connect() {
			ws.ConnectAsync();
		}

		public void Close() {
			ws.Close();

			sessionId = null;
			subscriptions.Clear();
			methodCalls.Clear();
		}

		void IDisposable.Dispose() {
			Close();
		}

		public Subscription Subscribe(string name, params JSONObject[] items) {
			Subscription subscription = new Subscription() {
				id = "" + subscriptionId++,
				name = name,
				items = items
			};
			subscriptions[subscription.id] = subscription;
			Send(GetSubscriptionMessage(subscription));
			return subscription;
		}

		public void Unsubscribe(Subscription subscription) {
			Send(GetUnsubscriptionMessage(subscription));
		}

		public MethodCall Call(string methodName, params JSONObject[] items) {
			MethodCall methodCall = new MethodCall() {
				id = "" + methodCallId++,
				methodName = methodName,
				items = items
			};
			methodCalls[methodCall.id] = methodCall;
			Send(GetMethodCallMessage(methodCall));
			return methodCall;
		}

	}

}
