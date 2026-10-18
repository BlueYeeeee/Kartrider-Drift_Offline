using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace KartDriftLocal
{
    sealed class Gateway : IDisposable
    {
        readonly ProfileStore store;
        readonly Dictionary<string, object> templates;
        readonly TcpListener listener;
        readonly UdpClient ping;
        readonly List<TcpClient> clients = new List<TcpClient>();
        volatile bool stopped;
        public volatile bool LoginComplete;
        public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
        public int PingPort { get { return ((IPEndPoint)ping.Client.LocalEndPoint).Port; } }
        readonly Action<string> log;
        public Gateway(ProfileStore profile, int port, Action<string> logger)
        {
            store = profile; log = logger; templates = Bundle.Templates();
            listener = new TcpListener(IPAddress.Loopback, port); ping = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            try { listener.Start(16); } catch { ping.Close(); throw; }
            ThreadPool.QueueUserWorkItem(delegate { Accept(); }); ThreadPool.QueueUserWorkItem(delegate { Echo(); });
        }
        void Echo()
        { while (!stopped) try { var peer = new IPEndPoint(IPAddress.Any, 0); byte[] data = ping.Receive(ref peer); if (peer.Address.Equals(IPAddress.Loopback) && data.Length == 92) ping.Send(data, data.Length, peer); } catch (SocketException) { if (!stopped) log("UDP receive failed"); } catch (ObjectDisposedException) { return; } }
        void Accept()
        {
            while (!stopped) try { TcpClient client = listener.AcceptTcpClient(); lock (clients) clients.Add(client); ThreadPool.QueueUserWorkItem(delegate { try { Handle(client.GetStream()); } catch (Exception error) { if (!stopped) log("connection: " + error.GetType().Name); } finally { GracefulClose(client); lock (clients) clients.Remove(client); } }); }
            catch (SocketException) { if (!stopped) log("TCP accept failed"); } catch (ObjectDisposedException) { return; }
        }
        static void GracefulClose(TcpClient client)
        {
            // A rejected PUT may already have body bytes in flight. Closing an
            // unread Windows socket resets it and can discard the error reply.
            try { client.Client.Shutdown(SocketShutdown.Send); var stream = client.GetStream(); stream.ReadTimeout = 200; byte[] discard = new byte[4096]; int remaining = 65536; while (remaining > 0) { int n = stream.Read(discard, 0, Math.Min(discard.Length, remaining)); if (n == 0) break; remaining -= n; } }
            catch (IOException) { } catch (SocketException) { } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
            finally { client.Close(); }
        }
        static byte[] Exact(Stream stream, int size)
        { var data = new byte[size]; int offset = 0; while (offset < size) { int n = stream.Read(data, offset, size - offset); if (n == 0) throw new EndOfStreamException(); offset += n; } return data; }
        static void Bytes(Stream stream, byte[] data) { stream.Write(data, 0, data.Length); }
        static void Text(Stream stream, string text) { Bytes(stream, Encoding.UTF8.GetBytes(text)); }
        static void Frame(Stream stream, int op, byte[] data)
        {
            using (var header = new MemoryStream()) { header.WriteByte((byte)(128 | op)); if (data.Length < 126) header.WriteByte((byte)data.Length); else { header.WriteByte((byte)(data.Length <= 65535 ? 126 : 127)); byte[] length = data.Length <= 65535 ? BitConverter.GetBytes((ushort)data.Length) : BitConverter.GetBytes((ulong)data.Length); Array.Reverse(length); header.Write(length, 0, length.Length); } Bytes(stream, header.ToArray()); Bytes(stream, data); }
        }
        static void Send(Stream stream, object message) { Frame(stream, 1, Encoding.ASCII.GetBytes(Codec.Encode(J.Write(message)))); }
        static void Http(Stream stream, int status, byte[] data = null, int? size = null, string contentType = "application/octet-stream")
        { data = data ?? new byte[0]; string reason; switch (status) { case 200: reason = "OK"; break; case 404: reason = "Not Found"; break; case 405: reason = "Method Not Allowed"; break; case 409: reason = "Conflict"; break; case 411: reason = "Length Required"; break; case 413: reason = "Content Too Large"; break; default: reason = "Bad Request"; break; } Text(stream, "HTTP/1.1 " + status + " " + reason + "\r\nContent-Type: " + contentType + "\r\nContent-Length: " + (size ?? data.Length) + "\r\nConnection: close\r\n\r\n"); Bytes(stream, data); }
        void Handle(NetworkStream stream)
        {
            stream.ReadTimeout = 10000; string head;
            // Read only the header, retaining pipelined body/frame bytes in the socket.
            using (var buffer = new MemoryStream()) { int match = 0; byte[] end = Encoding.ASCII.GetBytes("\r\n\r\n"); while (match < 4) { int b = stream.ReadByte(); if (b < 0) return; buffer.WriteByte((byte)b); match = b == end[match] ? match + 1 : (b == 13 ? 1 : 0); if (buffer.Length > 16384) { Http(stream, 400); return; } } head = Encoding.ASCII.GetString(buffer.ToArray()); }
            string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None), request = lines[0].Split(' '); if (request.Length != 3) { Http(stream, 400); return; }
            var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1).Where(v => v.Length > 0)) { int i = line.IndexOf(':'); if (i <= 0) { Http(stream, 400); return; } string key = line.Substring(0, i).Trim(), value = line.Substring(i + 1).Trim(); if (!headers.ContainsKey(key)) headers[key] = new List<string>(); headers[key].Add(value); }
            string path = request[1].Split('?')[0];
            log("HTTP " + request[0] + " " + path);
            if (path.StartsWith("/local-assets/", StringComparison.Ordinal))
            {
                string key = path.Substring(14); if (key.Length != 32 || key.Any(c => "0123456789abcdef".IndexOf(c) < 0)) { Http(stream, 404); return; }
                // Match the existing server: an invalid reservation always yields 404.
                using (var db = store.Open()) if (db.Query("SELECT 1 FROM assets WHERE key=?", key).Count == 0) { Http(stream, 404); return; }
                if (request[0] == "GET" || request[0] == "HEAD") { byte[] data; int status = store.ReadAsset(key, out data); Http(stream, status, request[0] == "GET" ? data : null, data == null ? (int?)null : data.Length); return; }
                if (request[0] != "PUT") { Http(stream, 405); return; }
                List<string> lengths; if (headers.ContainsKey("Transfer-Encoding") || headers.TryGetValue("Content-Length", out lengths) && lengths.Count > 1) { Http(stream, 400); return; }
                if (!headers.TryGetValue("Content-Length", out lengths)) { Http(stream, 411); return; } long length;
                if (!Int64.TryParse(lengths[0], out length)) { Http(stream, 400); return; } if (length < 0 || length > ProfileStore.MaxAssetBytes) { Http(stream, 413); return; } if (length == 0) { Http(stream, 400); return; }
                List<string> expect; if (headers.TryGetValue("Expect", out expect) && expect.Count == 1 && expect[0].Equals("100-continue", StringComparison.OrdinalIgnoreCase)) Text(stream, "HTTP/1.1 100 Continue\r\n\r\n");
                byte[] content; try { content = Exact(stream, (int)length); } catch (EndOfStreamException) { Http(stream, 400); return; } Http(stream, store.PutAsset(key, content)); return;
            }
            List<string> upgrade;
            if (!headers.TryGetValue("Upgrade", out upgrade) || !upgrade[0].Equals("websocket", StringComparison.OrdinalIgnoreCase)) { Http(stream, 200, Encoding.UTF8.GetBytes("{\"status\":\"ok\",\"code\":0}"), null, "application/json"); return; }
            string accept; using (var sha = SHA1.Create()) accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(headers["Sec-WebSocket-Key"][0] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            string response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n";
            List<string> protocol; if (headers.TryGetValue("Sec-WebSocket-Protocol", out protocol)) response += "Sec-WebSocket-Protocol: " + protocol[0].Split(',')[0].Trim() + "\r\n";
            Text(stream, response + "\r\n"); stream.ReadTimeout = System.Threading.Timeout.Infinite; bool bannerSent = false;
            while (!stopped)
            {
                byte[] h = Exact(stream, 2); int op = h[0] & 15; ulong size = (ulong)(h[1] & 127);
                if (size == 126 || size == 127) { byte[] extended = Exact(stream, size == 126 ? 2 : 8); Array.Reverse(extended); size = extended.Length == 2 ? BitConverter.ToUInt16(extended, 0) : BitConverter.ToUInt64(extended, 0); }
                J.Need(size <= 128 * 1024 * 1024, "WebSocket frame too large"); byte[] mask = (h[1] & 128) != 0 ? Exact(stream, 4) : null, data = Exact(stream, (int)size); if (mask != null) for (int i = 0; i < data.Length; i++) data[i] ^= mask[i % 4];
                if (op == 8) { Frame(stream, 8, data); return; } if (op == 9) { Frame(stream, 10, data); continue; } if (op != 1) continue;
                string plain; try { plain = Codec.Decode(Encoding.ASCII.GetString(data)); } catch (CryptographicException) { continue; } catch (FormatException) { continue; }
                int end = plain.LastIndexOf('}'); var q = J.D(J.Read(end < 0 ? plain : plain.Substring(0, end + 1))); var notifications = J.L(); var reply = Answer(q, notifications);
                if (!bannerSent && J.S(q["_type"]) == "PqLoginComplete" && J.Same(J.Map(reply, "result")["code"], 0)) { notifications.Add(StartupBanner.Create()); bannerSent = true; }
                Send(stream, reply); foreach (var notice in notifications) Send(stream, notice);
                if (J.S(q["_type"]) == "PqLoginComplete" && J.Same(J.Map(reply, "result")["code"], 0)) LoginComplete = true;
                log(J.S(q["_type"]) + " -> " + J.S(reply["_type"]) + " code=" + J.Map(reply, "result")["code"]);
            }
        }
        public Dictionary<string, object> Answer(Dictionary<string, object> q, List<object> notifications)
        {
            string t = J.S(q["_type"]); var result = Template(q);
            try
            {
                result = store.Apply(q, result);
                if (t == "PqServerTime") result["utc"] = J.Now();
                if (t == "PqAwsLatencyInfo")
                { var values = J.A(q["awsLatencyInfoList"]); var measured = J.L(); foreach (var v in values.Select(J.D)) measured.Add(J.M("region", J.S(v["region"]), "pingMs", J.I(v["pingMs"]))); notifications.Add(J.M("_type", "PcAwsLatencyInfo", "awsLatencyInfoList", measured)); }
                object notices = J.Get(result, "_localProfileNotifications"), body = J.Get(result, "_localPerformancePush"); result.Remove("_localProfileNotifications"); result.Remove("_localPerformancePush"); if (notices != null) notifications.AddRange(J.A(notices)); if (body != null) notifications.Add(body); return result;
            }
            catch (Exception error)
            {
                if (!(error is ArgumentException || error is KeyNotFoundException || error is InvalidOperationException || error is OverflowException)) throw;
                notifications.Clear(); bool typed = error is PerformanceUnavailable || new[] { "PqKartUpgrade", "PqKartGearSet", "PqKartGearReset", "PqCheckString", "PqLicenseStart", "PqTimeAttackUpdate", "PqRiderNameChangeInfo", "PqRiderNameChangeReserve", "PqRiderNameChangeConfirm", "PqRiderNameChangeReserveCancel", "PqRiderNameDuplicateCheck", "PqSetRiderName" }.Contains(t);
                return J.M("_type", typed ? "Pr" + t.Substring(2) : "PrPacket", "reqId", J.Get(q, "reqId"), "result", J.M("code", 10001), "desc", error is PerformanceUnavailable ? error.Message : "Local operation unavailable or invalid");
            }
        }
        Dictionary<string, object> Template(Dictionary<string, object> q)
        {
            string t = J.S(q["_type"]); var r = J.M("_type", "PrPacket", "reqId", J.Get(q, "reqId"), "result", J.M("code", 0), "desc", "");
            switch (t)
            {
                case "PqConvertKoin": r["_type"] = "PrConvertKoin"; r["changes"] = J.L(); return r;
                case "PqGetStickerStockDownloadList": J.Put(r, J.M("_type", "PrGetStickerStockDownloadList", "rev", 1, "maxStickerCount", 1000, "list", J.L())); return r;
                case "PqOutletProduct": J.Put(r, J.M("_type", "PrOutletProduct", "products", J.L(), "outletData", J.M("products_S", J.L(), "products_AB", J.L(), "updateDate", J.Now()), "soldout", true)); return r;
                case "PqShopBalanceNxA": J.Put(r, J.M("_type", "PrShopBalanceNxA", "currency", "", "totalBalance", "0")); return r;
            }
            object text; string name = J.Get(q, "name") as string;
            if ((name != null && templates.TryGetValue(t + "|" + name, out text)) || templates.TryGetValue(t, out text)) { r = J.D(J.Read(J.S(text))); r["reqId"] = J.Get(q, "reqId"); }
            else if (new[] { "Get", "List", "Info", "Records", "Rankers", "Data", "Status", "Query", "Check" }.Any(t.Contains)) r["_type"] = "Pr" + t.Substring(2);
            if (J.S(r["_type"]) == "PrTimeAttackStart") { string track = J.S(J.Get(q, "trackId", J.Get(r, "trackId", ""))); r["trackId"] = track; r["startTime"] = J.Now(); r["gameId"] = track + "_" + J.Millis(); }
            if (t == "PqLogin") { string now = J.Now(); r["loginTime"] = now; r["utcNow"] = now; r["accPlayTimeSec"] = 0; if (J.Get(r, "allianceBattle") is Dictionary<string, object>) J.Map(r, "allianceBattle")["group"] = ""; string region = J.S(r["serverRegionId"]); J.Need(region.Length > 0 && !region.Any(c => "=[];".Contains(c)), "Invalid region identifier"); J.Map(r, "serverSetting")["pingServers"] = region + "=[127.0.0.1:" + PingPort + "];"; }
            if (t == "PqOutletInfo") r["outletProbUrl"] = ""; return r;
        }
        public void Dispose()
        { stopped = true; listener.Stop(); ping.Close(); lock (clients) foreach (var client in clients.ToList()) client.Close(); }
    }
}
