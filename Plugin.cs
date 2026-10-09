using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using OTAPI;
using Terraria;
using Terraria.Localization;
using Terraria.Net;
using Terraria.Net.Sockets;
using TerrariaApi.Server;
using TShockAPI;

namespace ConnectionGuard
{
	/// <summary>
	/// 修复官方 TShock / TerrariaServerAPI 的一个连接处理缺陷：
	///
	/// <c>TerrariaApi.Server.Hooking.NetHooks.OnConnectionAccepted</c> 在槽位满时调用
	/// <c>Netplay.TcpListener.StopListening()</c>，但整个代码库没有任何地方会重新
	/// <c>StartListening()</c>（<c>LinuxTcpSocket</c> 里写的 <c>Netplay.IsListening = false</c>
	/// 也没有被读取）。于是只要爬虫把 <c>MaxSlots + ReservedSlots</c> 个 TCP 槽位占满，
	/// 监听器就会永久关闭，真实玩家再也连不进来，只能重启服务器。
	///
	/// 本插件做三件事：
	///   1. 踢掉悬空连接：官方 NetHooks 在满员时既不分配槽位也不踢掉这个连接，
	///      本插件把这种连接拒绝掉（发完包后关闭 socket，避免 fd 泄漏）。
	///   2. 打破卡死：没有 State==10 的玩家时专用服务端不跑 Main.Update，
	///      原版 idle 超时永不触发。本插件在 Netplay.UpdateConnectedClients 上主动释放卡住的槽位。
	///   3. 恢复监听：有空闲槽位时重新 StartListening()。
	/// 另外按 IP 对短时间内的海量新建连接做临时封禁，从源头减少爬虫占用槽位。
	/// </summary>
	[ApiVersion(2, 1)]
	public class ConnectionGuard : TerrariaPlugin
	{
		public override string Name => "ConnectionGuard";

		public override string Author => "hufang360";

		public override string Description => "修复槽位满后永久停止监听的问题，并限制爬虫连接频率";

		public override Version Version => Assembly.GetExecutingAssembly().GetName().Version;

		private static readonly string SaveDir = Path.Combine(TShock.SavePath, "ConnectionGuard");

		private static readonly string ConfigPath = Path.Combine(SaveDir, "config.json");

		private static GuardConfig _config = new GuardConfig();

		private static readonly object _lock = new object();

		private readonly Dictionary<string, List<DateTime>> _attempts = new Dictionary<string, List<DateTime>>();

		private readonly Dictionary<string, DateTime> _bans = new Dictionary<string, DateTime>();

		private readonly DateTime[] _slotSince = new DateTime[256];

		private readonly int[] _slotState = new int[256];

		private DateTime _nextListenRetry = DateTime.MinValue;

		private DateTime _nextCleanup = DateTime.MinValue;

		private DateTime _nextErrorLog = DateTime.MinValue;

		public ConnectionGuard(Main game) : base(game)
		{
		}

		public override void Initialize()
		{
			LoadConfig();

			// 这两个是 OTAPI 的静态事件。TerrariaServerAPI 的 NetHooks 在插件加载之前就订阅了，
			// 因此我们的 handler 一定在它之后执行 —— 正好能观察到它“分配了槽位 / 停掉监听”的结果。
			HookEvents.Terraria.Netplay.OnConnectionAccepted += OnConnectionAccepted;
			HookEvents.Terraria.Netplay.ServerFullWriteCallBack += OnServerFullWriteCallBack;

			// 注意：不能用 ServerApi.Hooks.GameUpdate —— 专用服务端在没有任何“已完成连接”的玩家时
			// 不会调用 Main.Update（见 OTAPI 里 `if (Netplay.HasFullyConnectedClients) Update(...)`），
			// 而那正是被爬虫占满后的状态，GameUpdate 根本不会触发。
			// Netplay 的服务器循环线程始终在跑，所以把看门狗挂在 UpdateConnectedClients 上。
			HookEvents.Terraria.Netplay.UpdateConnectedClients += OnUpdateConnectedClients;

			TShock.Log.ConsoleInfo(string.Format(
				"[ConnectionGuard] 已启用：监听修复=开，连接限流={0}（{1} 次/{2} 秒，临时封禁 {3} 分钟）。",
				_config.EnableRateLimit ? "开" : "关",
				_config.MaxConnectsPerWindow, _config.WindowSeconds, _config.BanMinutes));
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				HookEvents.Terraria.Netplay.OnConnectionAccepted -= OnConnectionAccepted;
				HookEvents.Terraria.Netplay.ServerFullWriteCallBack -= OnServerFullWriteCallBack;
				HookEvents.Terraria.Netplay.UpdateConnectedClients -= OnUpdateConnectedClients;
			}

			base.Dispose(disposing);
		}

		private void OnConnectionAccepted(object sender, HookEvents.Terraria.Netplay.OnConnectionAcceptedEventArgs args)
		{
			ISocket client = args.client;
			if (client == null)
			{
				return;
			}

			string ip = GetIp(client);

			if (_config.EnableRateLimit && ip.Length > 0 && ip != "127.0.0.1")
			{
				if (IsBanned(ip))
				{
					Reject(client, NetworkText.FromLiteral("Your IP is temporarily blocked for connection flooding."));
					return;
				}

				if (ExceedsRate(ip))
				{
					Ban(ip);
					Reject(client, NetworkText.FromLiteral("Too many connections from your IP."));
					return;
				}
			}

			// 没有被分配到任何槽位 = 服务器满了：官方 NetHooks 只是在满员时 StopListening，
			// 并不会踢掉这个悬空连接，这里补上，否则它会一直挂着占用 fd。
			int slot = FindAssignedSlot(client);
			if (slot >= 0)
			{
				_slotSince[slot] = DateTime.UtcNow;
				_slotState[slot] = Netplay.Clients[slot].State;
			}
			else
			{
				Reject(client, NetworkText.FromKey("CLI.ServerIsFull"));
			}
		}

		/// <summary>
		/// <c>Netplay.KickClient</c> 发送完“服务器已满 / 被拒绝”的包后会回调这里。
		/// 此时关闭 socket，保证被拒绝的连接不会泄漏。
		/// </summary>
		private void OnServerFullWriteCallBack(object sender, HookEvents.Terraria.Netplay.ServerFullWriteCallBackEventArgs args)
		{
			ISocket socket = args.state as ISocket;
			if (socket != null)
			{
				try { socket.Close(); } catch { }
			}
		}

		private void OnUpdateConnectedClients(object sender, HookEvents.Terraria.Netplay.UpdateConnectedClientsEventArgs args)
		{
			// 这个 handler 跑在服务端的网络循环线程上，一旦抛异常会把 ServerLoop 带崩，
			// 所以整体包一层 try/catch。
			try
			{
				DateTime now = DateTime.UtcNow;

				if (now >= _nextCleanup)
				{
					_nextCleanup = now.AddSeconds(60);
					Cleanup(now);
				}

				// 死锁修复：槽位被“只连不发”的爬虫占满后，没有任何 State==10 的玩家，
				// 专用服务端就不再调用 Main.Update，于是原版的 idle 超时与 CheckBytes 永远不执行，
				// 槽位永远不释放。只要当前没有已进游戏的玩家，就持续活跃清理卡住的连接。
				if (!Netplay.Disconnect && !Netplay.HasFullyConnectedClients)
				{
					EvictStuckClients(now);
				}

				// 核心修复：官方代码停掉监听后永不恢复，这里在有空槽时把监听拉起来。
				if (Netplay.Disconnect || Netplay.IsListening)
				{
					return;
				}

				if (now < _nextListenRetry)
				{
					return;
				}

				if (Netplay.FindNextOpenClientSlot() == -1)
				{
					return; // 仍然满员，等有槽位再说
				}

				if (Netplay.StartListening())
				{
					Netplay.IsListening = true;
					TShock.Log.ConsoleInfo("[ConnectionGuard] 有空闲槽位，已恢复端口监听。");
				}
				else
				{
					_nextListenRetry = now.AddSeconds(5);
				}
			}
			catch (Exception ex)
			{
				_nextListenRetry = DateTime.UtcNow.AddSeconds(5);
				if (DateTime.UtcNow >= _nextErrorLog)
				{
					_nextErrorLog = DateTime.UtcNow.AddSeconds(30);
					TShock.Log.ConsoleError("[ConnectionGuard] 恢复端口监听失败：" + ex);
				}
			}
		}

		/// <summary>
		/// 在没有已进游戏的玩家（Main.Update 不运行）时，把长时间停留在“已连接但未完成握手”
		/// 状态的连接直接 Reset，释放槽位。客户端每次握手状态前进（State 变化）都会重置计时，
		/// 所以正在加载世界的真人不会被误伤，而卡在 State==0 的爬虫会被清掉。
		/// </summary>
		private void EvictStuckClients(DateTime now)
		{
			int seconds = _config.StuckTimeoutSeconds;
			int max = Main.maxNetPlayers;
			for (int i = 0; i < max; i++)
			{
				RemoteClient client = Netplay.Clients[i];
				if (client == null)
				{
					continue;
				}

				if (!client.IsConnected())
				{
					_slotSince[i] = DateTime.MinValue;
					_slotState[i] = 0;
					continue;
				}

				// State 10 = 已完成握手 / 已进入游戏的玩家，交给原版逻辑处理。
				if (client.State >= 10)
				{
					continue;
				}

				// 握手有进展就重新计时，避免误伤正在加载世界的玩家。
				if (client.State != _slotState[i])
				{
					_slotState[i] = client.State;
					_slotSince[i] = now;
					continue;
				}

				DateTime since = _slotSince[i];
				if (since == DateTime.MinValue)
				{
					_slotSince[i] = now;
					continue;
				}

				if ((now - since).TotalSeconds < seconds)
				{
					continue;
				}

				TShock.Log.ConsoleInfo(string.Format(
					"[ConnectionGuard] 强制释放卡住槽位 {0}（{1}，State={2}，已停留 {3:0} 秒）。",
					i, TryGetIp(client), client.State, (now - since).TotalSeconds));
				try
				{
					client.Reset();
				}
				catch (Exception ex)
				{
					TShock.Log.ConsoleError("[ConnectionGuard] 释放槽位失败：" + ex.Message);
				}
				_slotSince[i] = DateTime.MinValue;
				_slotState[i] = 0;
			}
		}

		#region 工具方法

		private static int FindAssignedSlot(ISocket client)
		{
			int max = Main.maxNetPlayers;
			for (int i = 0; i < max; i++)
			{
				if (ReferenceEquals(Netplay.Clients[i].Socket, client))
				{
					return i;
				}
			}
			return -1;
		}

		private static string TryGetIp(RemoteClient client)
		{
			try
			{
				if (client.Socket == null)
				{
					return "?";
				}
				RemoteAddress addr = client.Socket.GetRemoteAddress();
				return addr != null ? addr.GetIdentifier() : "?";
			}
			catch
			{
				return "?";
			}
		}

		private static string GetIp(ISocket client)
		{
			try
			{
				RemoteAddress addr = client.GetRemoteAddress();
				if (addr == null || addr.Type != AddressType.Tcp)
				{
					return string.Empty;
				}
				return addr.GetIdentifier() ?? string.Empty;
			}
			catch
			{
				return string.Empty;
			}
		}

		/// <summary>发送拒绝消息；发送完成后由 <see cref="OnServerFullWriteCallBack"/> 关闭连接。</summary>
		private static void Reject(ISocket client, NetworkText message)
		{
			try
			{
				// KickClient 内部使用共享的 fullBuffer，vanilla 也是在 fullBuffer 上加锁调用的。
				lock (Netplay.fullBuffer)
				{
					Netplay.KickClient(client, message);
				}
			}
			catch
			{
				try { client.Close(); } catch { }
			}
		}

		private bool IsBanned(string ip)
		{
			lock (_lock)
			{
				DateTime until;
				if (_bans.TryGetValue(ip, out until))
				{
					if (DateTime.UtcNow < until)
					{
						return true;
					}
					_bans.Remove(ip);
				}
			}
			return false;
		}

		private bool ExceedsRate(string ip)
		{
			DateTime now = DateTime.UtcNow;
			lock (_lock)
			{
				List<DateTime> times;
				if (!_attempts.TryGetValue(ip, out times))
				{
					times = new List<DateTime>();
					_attempts[ip] = times;
				}

				times.RemoveAll(t => (now - t).TotalSeconds > _config.WindowSeconds);
				times.Add(now);
				return times.Count > _config.MaxConnectsPerWindow;
			}
		}

		private void Ban(string ip)
		{
			DateTime until = DateTime.UtcNow.AddMinutes(_config.BanMinutes);
			lock (_lock)
			{
				_bans[ip] = until;
				_attempts.Remove(ip);
			}
			TShock.Log.ConsoleInfo(string.Format(
				"[ConnectionGuard] 已临时封禁 {0} 至 {1:HH:mm:ss}（短时间内连接过于频繁）。",
				ip, until.ToLocalTime()));
		}

		private void Cleanup(DateTime now)
		{
			lock (_lock)
			{
				List<string> stale = null;
				foreach (KeyValuePair<string, List<DateTime>> kv in _attempts)
				{
					List<DateTime> list = kv.Value;
					if (list.Count == 0 || (now - list[list.Count - 1]).TotalSeconds > _config.WindowSeconds)
					{
						if (stale == null)
						{
							stale = new List<string>();
						}
						stale.Add(kv.Key);
					}
				}
				if (stale != null)
				{
					foreach (string key in stale)
					{
						_attempts.Remove(key);
					}
				}

				List<string> expired = null;
				foreach (KeyValuePair<string, DateTime> kv in _bans)
				{
					if (now >= kv.Value)
					{
						if (expired == null)
						{
							expired = new List<string>();
						}
						expired.Add(kv.Key);
					}
				}
				if (expired != null)
				{
					foreach (string key in expired)
					{
						_bans.Remove(key);
					}
				}
			}
		}

		private static void LoadConfig()
		{
			try
			{
				if (!Directory.Exists(SaveDir))
				{
					Directory.CreateDirectory(SaveDir);
				}

				if (File.Exists(ConfigPath))
				{
					GuardConfig loaded = JsonConvert.DeserializeObject<GuardConfig>(File.ReadAllText(ConfigPath));
					if (loaded != null)
					{
						_config = loaded;
					}
				}
				else
				{
					File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(_config, Formatting.Indented));
				}

				// 防止手改配置写出 0 / 负数导致逻辑异常。
				_config.WindowSeconds = Math.Max(1, _config.WindowSeconds);
				_config.MaxConnectsPerWindow = Math.Max(1, _config.MaxConnectsPerWindow);
				_config.BanMinutes = Math.Max(1, _config.BanMinutes);
				_config.StuckTimeoutSeconds = Math.Max(5, _config.StuckTimeoutSeconds);
			}
			catch (Exception ex)
			{
				TShock.Log.ConsoleError("[ConnectionGuard] 读取配置失败，使用默认值：" + ex.Message);
			}
		}

		#endregion
	}

	public class GuardConfig
	{
		/// <summary>是否开启按 IP 的连接限流。</summary>
		public bool EnableRateLimit = true;

		/// <summary>限流统计的时间窗口（秒）。</summary>
		public int WindowSeconds = 10;

		/// <summary>时间窗口内允许的最大新建连接数，超过则临时封禁。</summary>
		public int MaxConnectsPerWindow = 10;

		/// <summary>临时封禁时长（分钟）。</summary>
		public int BanMinutes = 10;

		/// <summary>监听停止后，连接卡在“未完成握手”状态多久就强制释放槽位（秒）。</summary>
		public int StuckTimeoutSeconds = 60;
	}
}
