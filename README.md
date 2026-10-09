# ConnectionGuard

修复 TShock / TerrariaServerAPI 在服务器槽位占满后**永久停止监听、真实玩家再也连不进来**的问题，
并顺带限制爬虫的高频新建连接。无需重新编译服务端，把 `ConnectionGuard.dll` 丢进 `plugins/` 即可。

## 它修的是什么

`TerrariaApi.Server.Hooking.NetHooks.OnConnectionAccepted`（TerrariaServerAPI 子模块）在槽位满时：

```csharp
if (FindNextOpenClientSlot() == -1)
{
    Netplay.TcpListener?.StopListening();   // 满了就关监听
}
```

而整个代码库里**没有任何地方会再次 `StartListening()`**。`TShockAPI/Sockets/LinuxTcpSocket.cs`
里虽然写了 `Netplay.IsListening = false;` 并附了“ServerLoop 会重新监听”的注释，但
`Netplay.IsListening` 从未被任何代码读取，这个“自动恢复”逻辑根本不存在。

后果：

- 服务器槽位数 = `MaxSlots + ReservedSlots`（默认 8 + 20 = 28）。
- 爬虫只要凑够 28 个 TCP 连接把槽位占满，监听器就会在下一次连接进来后真正关闭。
- 之后所有新连接（包括真实玩家）都会被拒绝，**只能重启服务器**。

官方镜像 `ghcr.io/pryaxis/tshock:6.2.1` 同样存在该问题（对应 TSAPI `general-devel`）。

## 它做了什么

1. **踢掉悬空连接**
   官方 NetHooks 满员时既不分配槽位、也不踢掉这个 socket，只调用 `StopListening()`，
   于是这个连接会一直挂着泄漏 fd。本插件在 `OnConnectionAccepted` 末尾补刀：
   如果这个 `client` 没有被分配到任何槽位，就 `Netplay.KickClient` 拒绝它，
   并在数据发完后关闭 socket。

2. **清理卡死的槽位（死锁的关键）**
   原版的 idle 超时与 `Netplay.CheckBytes` 都写在 `Main.Update` 里，而专用服务端只有在
   `Netplay.HasFullyConnectedClients == true`（存在 `State == 10` 的玩家）时才会调用
   `Main.Update`（OTAPI：`if (Netplay.HasFullyConnectedClients) Update(...)`）。
   爬虫的裸连接永远停在 `State == 0`，所以一旦它们占满槽位，就形成死锁：

   > 没有已完成连接的玩家 → 不跑 `Main.Update` → 槽位永不超时释放 → 永远没有已完成连接的玩家。

   插件挂在 `Netplay.UpdateConnectedClients`（服务端网络循环线程，**任何时候都在跑**）上：
   只要当前没有已进游戏的玩家，就把停留超过 `StuckTimeoutSeconds`、且 `State < 10` 的连接直接
   `Reset()`，释放槽位。

3. **恢复监听（核心修复）**
   一旦出现空闲槽位，且 `Netplay.IsListening == false`，就调用 `Netplay.StartListening()`
   把监听拉起来，并把 `Netplay.IsListening` 置回 `true`。

4. **按 IP 限流 / 临时封禁（默认开启）**
   统计每个 IP 在时间窗口内的新建连接数，超阈值就临时封禁，从源头减少爬虫占用槽位。
   NAT / 真人一般不会在 10 秒内新建 10 次连接；日志里那种同一 IP 刷几十个端口号的行为会被拦下。

## 构建

```bash
cd ~/tr/dev/ConnectionGuard
dotnet build -c Release
# 产物：bin/Release/net9.0/ConnectionGuard.dll
```

## 部署

把 DLL 复制到插件目录并重启容器：

```bash
cp ~/tr/dev/ConnectionGuard/bin/Release/net9.0/ConnectionGuard.dll ~/tr/s/ts/plugins/
docker restart tshock && docker attach tshock
```

启动日志里应出现：

```
Plugin ConnectionGuard v1.0.0.0 (by hufang360) initiated.
[ConnectionGuard] 已启用：监听修复=开，连接限流=开（10 次/10 秒，临时封禁 10 分钟）。
```

## 配置

首次启动会在 `<TShock.SavePath>/ConnectionGuard/config.json`（容器内 `/tshock/ConnectionGuard/config.json`）
生成配置：

```json
{
  "EnableRateLimit": true,
  "WindowSeconds": 10,
  "MaxConnectsPerWindow": 10,
  "BanMinutes": 10,
  "StuckTimeoutSeconds": 60
}
```

- `EnableRateLimit`：是否开启按 IP 限流。个人小服可设 `false` 只保留监听修复。
- `MaxConnectsPerWindow`：窗口内允许的最大新建连接数。真人重连较多时调大，如 `20`。
- `BanMinutes`：临时封禁时长（仅内存，重启后清空）。
- `StuckTimeoutSeconds`：没有已进游戏的玩家时，连接卡在“未完成握手”状态多久就强制释放槽位。
  真人正常进服几秒内就会达到 `State == 10`，一般不会误伤；网络很慢可调大。

## 兼容性

- 与 `ghcr.io/pryaxis/tshock:6.2.1` 及对应 TSAPI 编译（同一份代码在 `ghcr.io/pryaxis/tshock:stable` 上也一样）。
- 如果你同时使用了 `TerrariaServerAPI` 子模块里的 `NetHooks` 补丁（把 `StopListening` 换成 `KickClient`），
  本插件依然工作；仅在“服务器满”这一瞬间可能多发一个 `server is full` 包，无副作用。

## 注意

监听修复解决的是“被占满后永久失联”，但爬虫占槽位期间真人仍可能看到“服务器已满”。
要真正减少噪音，建议同时：

1. 把游戏端口从默认 `7777` 改成不常用端口（爬虫基本只扫 7777）。
2. 防火墙只放行玩家来源，或封掉云厂商网段（日志里的 `34.x` 是 GCP）。
3. 不要把 REST API 端口 `7878` 暴露到公网。
