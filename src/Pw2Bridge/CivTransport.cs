// SPDX-License-Identifier: GPL-2.0-or-later
//
// Byte transports for the CI-V link. Pw2CivSerial does all the framing,
// timing and command logic; it only needs a few primitives from the thing
// that actually moves bytes. Two implementations:
//
//   SerialCivTransport  a local COM/tty port (the single-PC station)
//   TcpCivTransport     a raw TCP socket to a serial server (a station hub
//                       sharing the amp's port on the LAN, one client at a time)
//
// Both are synchronous and single-threaded by contract: Pw2CivSerial holds its
// own lock around every call, so transports never need their own.

using System;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Threading;

namespace Zeus.Community.Pw2Bridge;

/// <summary>The handful of operations the CI-V layer needs from a byte link.</summary>
public interface ICivTransport : IDisposable
{
    /// <summary>True while the link is believed usable. Flips false on an I/O error or remote close.</summary>
    bool IsOpen { get; }

    /// <summary>Short human description for logs and the status panel, e.g. "COM10 @ 9600" or "tcp://hub:4001".</summary>
    string Description { get; }

    /// <summary>Write all bytes. Throws on failure.</summary>
    void Write(byte[] data);

    /// <summary>
    /// Read up to <paramref name="buffer"/>.Length bytes, waiting at most
    /// <paramref name="timeoutMs"/> for the first byte. Returns 0 on timeout.
    /// Throws if the link has failed (including an orderly close by the peer).
    /// </summary>
    int Read(byte[] buffer, int timeoutMs);

    /// <summary>Drop any bytes already received but not yet read.</summary>
    void DiscardInput();

    /// <summary>Close the link. Safe to call more than once.</summary>
    void Close();
}

/// <summary>Local serial port transport. Settings mirror the original COM-port code.</summary>
public sealed class SerialCivTransport : ICivTransport
{
    private SerialPort? _port;

    public string Description { get; }

    public bool IsOpen => _port is { IsOpen: true };

    /// <summary>Open the port. Throws (with the OS message) if it can't.</summary>
    public SerialCivTransport(string portName, int baud, int stopBits)
    {
        var p = new SerialPort(portName, baud, Parity.None, 8,
            stopBits == 2 ? StopBits.Two : StopBits.One)
        {
            ReadTimeout = 20,   // fine granularity; Pw2CivSerial loops to its own deadline
            WriteTimeout = 500,
            // Guide p.2: RTS tied to CTS, comms without flow control.
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
        };
        p.Open();
        _port = p;
        Description = $"{portName} @ {baud} 8N{stopBits}";
    }

    public void Write(byte[] data)
    {
        var p = _port ?? throw new IOException("serial port closed");
        p.Write(data, 0, data.Length);
    }

    public int Read(byte[] buffer, int timeoutMs)
    {
        var p = _port ?? throw new IOException("serial port closed");
        // SerialPort.ReadTimeout is fixed at open; honour the caller's budget by
        // looping in 20 ms slices. In practice the caller passes 20.
        var deadline = Environment.TickCount64 + Math.Max(timeoutMs, 1);
        do
        {
            try { return p.Read(buffer, 0, buffer.Length); }
            catch (TimeoutException) { /* no byte yet */ }
        } while (Environment.TickCount64 < deadline);
        return 0;
    }

    public void DiscardInput()
    {
        try { _port?.DiscardInBuffer(); } catch { /* ignore */ }
    }

    public void Close()
    {
        try { if (_port is { IsOpen: true }) _port.Close(); } catch { /* ignore */ }
        _port?.Dispose();
        _port = null;
    }

    public void Dispose() => Close();

    /// <summary>Names of the serial ports the OS currently knows about.</summary>
    public static string[] ListPorts()
    {
        try { return SerialPort.GetPortNames(); } catch { return Array.Empty<string>(); }
    }
}

/// <summary>
/// Raw TCP transport to a serial server (ser2net-style "raw" mode, or the
/// station hub). The server owns the physical port and its framing; we just
/// exchange CI-V bytes over the socket.
///
/// A hub that refuses a second client simply closes the socket; that shows up
/// here as an orderly close on the first Read and is reported as such.
/// </summary>
public sealed class TcpCivTransport : ICivTransport
{
    /// <summary>Default TCP port a serial server exposes the amp on.</summary>
    public const int DefaultPort = 4001;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    private Socket? _sock;
    private bool _open;

    public string Description { get; }

    public bool IsOpen => _open && _sock is { Connected: true };

    /// <summary>Connect. Throws (connection refused, timeout, unknown host) if it can't.</summary>
    public TcpCivTransport(string host, int port)
    {
        Description = $"tcp://{host}:{port}";
        var s = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,      // CI-V packets are tiny; never let Nagle sit on one
            SendTimeout = 500,
        };
        try
        {
            using var cts = new CancellationTokenSource(ConnectTimeout);
            try
            {
                s.ConnectAsync(host, port, cts.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"connect to {host}:{port} timed out after {ConnectTimeout.TotalSeconds:0}s");
            }
        }
        catch
        {
            s.Dispose();
            throw;
        }
        _sock = s;
        _open = true;
    }

    public void Write(byte[] data)
    {
        var s = _sock ?? throw new IOException("socket closed");
        try
        {
            int sent = 0;
            while (sent < data.Length)
                sent += s.Send(data, sent, data.Length - sent, SocketFlags.None);
        }
        catch (SocketException ex)
        {
            _open = false;
            if (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown)
                throw new IOException("connection closed by the serial server (port busy, or server stopped)", ex);
            throw new IOException($"send failed: {ex.Message}", ex);
        }
    }

    public int Read(byte[] buffer, int timeoutMs)
    {
        var s = _sock ?? throw new IOException("socket closed");
        try
        {
            // Poll returns true when data is readable OR the peer has closed;
            // a zero-byte Receive distinguishes the two.
            if (!s.Poll(Math.Max(timeoutMs, 1) * 1000, SelectMode.SelectRead))
                return 0;
            int n = s.Receive(buffer, 0, buffer.Length, SocketFlags.None);
            if (n == 0)
            {
                _open = false;
                throw new IOException("connection closed by the serial server (port busy, or server stopped)");
            }
            return n;
        }
        catch (SocketException ex)
        {
            _open = false;
            // A reset is what we get when the server closed while our command
            // was still unread; report it the same way as an orderly close.
            if (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown)
                throw new IOException("connection closed by the serial server (port busy, or server stopped)", ex);
            throw new IOException($"receive failed: {ex.Message}", ex);
        }
    }

    public void DiscardInput()
    {
        var s = _sock;
        if (s is null) return;
        try
        {
            var junk = new byte[256];
            while (s.Available > 0)
            {
                int n = s.Receive(junk, 0, Math.Min(junk.Length, s.Available), SocketFlags.None);
                if (n == 0) { _open = false; break; }   // peer closed; surface on next Read
            }
        }
        catch { /* ignore; a real failure surfaces on the next Read/Write */ }
    }

    public void Close()
    {
        _open = false;
        var s = _sock;
        _sock = null;
        if (s is null) return;
        try { s.Shutdown(SocketShutdown.Both); } catch { /* ignore */ }
        try { s.Close(); } catch { /* ignore */ }
    }

    public void Dispose() => Close();
}
