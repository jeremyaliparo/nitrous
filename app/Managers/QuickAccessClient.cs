using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Nitrous.Diagnostics;

namespace Nitrous.Managers;

/// <summary>
/// Client for Acer's QuickAccess agent, which listens on <c>wss://localhost:5141</c>.
///
/// This is a different transport from the WMI interface Nitrous uses elsewhere. On models where
/// power-off USB charging is not exposed through <c>APGeAction</c>, the newer NitroSense reaches it
/// here instead, so both paths are tried.
///
/// The handshake is Acer's own: the client sends an AES-ECB encrypted
/// <c>{"Question": ..., "Key": ...}</c> and the agent answers with the question encrypted under the
/// second key. Both keys are constants in Acer's shipped software; the exchange only proves the
/// peer knows them, so it is a fixed challenge rather than a secret.
///
/// Every operation opens its own connection. The agent is local, so the cost is small, and it avoids
/// holding a socket open in a tray process that may run for days.
/// </summary>
public static class QuickAccessClient
{
    private const string Endpoint = "wss://localhost:5141";
    private const string Question = "AcerQuickAccessFunctionalityTests_HandshakingQuestion";

    /// <summary>Encrypts the client's auth packet.</summary>
    private const string AuthKey = "A6052DC8A6E44210";

    /// <summary>Sent inside the auth packet and used to check the agent's answer.</summary>
    private const string ChallengeKey = "AB252AB73BED1CDB";

    private const int AuthPacket = 1;
    private const int RequestPacket = 2;
    private const int ResponsePacket = 3;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Reads power-off USB charging. False when the agent is not running, which is the expected
    /// result on a machine where Acer's services have been disabled.
    /// </summary>
    public static bool TryGetUsbCharging(out bool on, out int? floor)
    {
        on = false;
        floor = null;

        var switchResult = Query("USBChargeSwitch", action: "Get", param: null);
        if (switchResult is not { } switchReply) return false;

        on = switchReply != 0;

        var floorResult = Query("USBCharge", action: "Get", param: null);
        if (floorResult is { } reported && AcerWmiProtocol.UsbChargingFloors.Contains(reported))
            floor = reported;

        return true;
    }

    /// <summary>
    /// Writes both fields. The agent takes them as separate commands, so the switch is set first and
    /// the battery floor second; a failure in either reports failure rather than a partial success.
    /// </summary>
    public static bool TrySetUsbCharging(bool on, int floor)
    {
        if (!AcerWmiProtocol.UsbChargingFloors.Contains(floor)) return false;

        if (Query("USBChargeSwitch", "Set", on ? 1 : 0) is null) return false;
        return Query("USBCharge", "Set", floor) is not null;
    }

    /// <summary>
    /// Sends one command and returns its numeric result. Null means the agent did not answer, which
    /// callers treat as "not available here" rather than as a failure of the feature.
    /// </summary>
    private static int? Query(string command, string action, int? param)
    {
        try
        {
            using var socket = new ClientWebSocket();

            // The agent is a loopback endpoint with a self-signed certificate. Acer's own client
            // does not validate it either, and the connection never leaves the machine.
            socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

            using var timeout = new CancellationTokenSource(Timeout);
            socket.ConnectAsync(new Uri(Endpoint), timeout.Token).GetAwaiter().GetResult();

            if (!Handshake(socket, timeout.Token))
            {
                Logger.Warn("QuickAccess: the agent answered the handshake incorrectly.");
                return null;
            }

            var session = Guid.NewGuid().ToString();
            var request = new Dictionary<string, object?>
            {
                ["PacketType"] = RequestPacket,
                ["Version"] = 1,
                ["Session"] = session,
                ["Command"] = command,
                ["Action"] = action
            };

            if (param is { } value) request["Param1"] = value;

            Send(socket, JsonSerializer.Serialize(request), timeout.Token);

            var reply = Receive(socket, timeout.Token);
            if (reply is not { } document) return null;

            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("PacketType", out var type) ||
                    type.GetInt32() != ResponsePacket)
                {
                    return null;
                }

                if (!root.TryGetProperty("Result", out var result) ||
                    !result.TryGetProperty("Value", out var resultValue))
                {
                    return null;
                }

                return resultValue.ValueKind switch
                {
                    JsonValueKind.True => 1,
                    JsonValueKind.False => 0,
                    JsonValueKind.Number => resultValue.GetInt32(),
                    _ => null
                };
            }
        }
        catch (Exception ex)
        {
            Logger.Info($"QuickAccess: {command} {action} did not answer ({ex.GetType().Name}).");
            return null;
        }
    }

    private static bool Handshake(ClientWebSocket socket, CancellationToken token)
    {
        var payload = JsonSerializer.Serialize(new { Question, Key = ChallengeKey });

        var session = Guid.NewGuid().ToString();
        var auth = new
        {
            PacketType = AuthPacket,
            Session = session,
            Version = 1,
            Data = Encrypt(payload, AuthKey)
        };

        Send(socket, JsonSerializer.Serialize(auth), token);

        var reply = Receive(socket, token);
        if (reply is not { } document) return false;

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("PacketType", out var type) || type.GetInt32() != AuthPacket)
                return false;

            if (!root.TryGetProperty("Data", out var data)) return false;

            return string.Equals(data.GetString(), Encrypt(Question, ChallengeKey), StringComparison.Ordinal);
        }
    }

    private static string Encrypt(string text, string key)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = Encoding.UTF8.GetBytes(key);

        using var encryptor = aes.CreateEncryptor();
        var bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToBase64String(encryptor.TransformFinalBlock(bytes, 0, bytes.Length));
    }

    private static void Send(ClientWebSocket socket, string json, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token)
              .GetAwaiter().GetResult();
    }

    private static JsonDocument? Receive(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = socket.ReceiveAsync(new ArraySegment<byte>(buffer), token)
                               .GetAwaiter().GetResult();

            if (result.MessageType == WebSocketMessageType.Close) return null;
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }

        if (stream.Length == 0) return null;

        try
        {
            return JsonDocument.Parse(stream.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
