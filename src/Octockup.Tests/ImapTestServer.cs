// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Microsoft.Extensions.Logging.Abstractions;
using Octockup.Server.Modules;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Octockup.Tests
{
    public class ImapTestServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _server;
        private readonly int _messageCount;
        private int _bodyRequests;
        private string? _selectedFolder;

        public ImapTestServer(int messageCount)
        {
            _messageCount = messageCount;
            _listener.Start();
            _server = ServeAsync();
        }

        public int BodyRequests => Volatile.Read(ref _bodyRequests);
        public ConcurrentQueue<string> SummaryFolders { get; } = new();

        public IMAPSource CreateSource(int batchSize = 1_000)
        {
            IMAPSource source = new(NullLogger<IMAPSource>.Instance);
            source.SetParameters(new Dictionary<string, string>
            {
                ["host"] = "127.0.0.1",
                ["port"] = ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(),
                ["username"] = "account",
                ["password"] = "test-password",
                ["path"] = "INBOX",
                ["useSsl"] = "false",
                ["batchSize"] = batchSize.ToString(),
            });
            return source;
        }

        private async Task ServeAsync()
        {
            try
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                await using NetworkStream stream = client.GetStream();
                using StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
                await using StreamWriter writer = new(stream, Encoding.ASCII, leaveOpen: true)
                {
                    NewLine = "\r\n",
                    AutoFlush = true,
                };
                await writer.WriteLineAsync("* OK [CAPABILITY IMAP4rev1] Test mail server");
                while (await reader.ReadLineAsync(_cancellation.Token) is string line)
                {
                    string[] parts = line.Split(' ', 3);
                    string command = parts[1].ToUpperInvariant();
                    await RespondAsync(writer, command, parts.ElementAtOrDefault(2) ?? string.Empty);
                    string access = command is "SELECT" or "EXAMINE" ? "[READ-ONLY] " : string.Empty;
                    await writer.WriteLineAsync($"{parts[0]} OK {access}Completed");
                    if (command == "LOGOUT")
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
            }
        }

        private async Task RespondAsync(StreamWriter writer, string command, string arguments)
        {
            switch (command)
            {
                case "CAPABILITY":
                    await writer.WriteLineAsync("* CAPABILITY IMAP4rev1");
                    break;
                case "LOGIN":
                    break;
                case "LIST":
                    string folder = arguments.Split(' ')[^1].Trim('"');
                    string attributes = folder.Length == 0 ? "\\Noselect" : "\\HasNoChildren";
                    await writer.WriteLineAsync($"* LIST ({attributes}) \"/\" \"{folder}\"");
                    break;
                case "SELECT":
                case "EXAMINE":
                    _selectedFolder = arguments.Trim('"');
                    int count = _selectedFolder == "INBOX" ? _messageCount : 2;
                    await writer.WriteLineAsync("* FLAGS (\\Seen)");
                    await writer.WriteLineAsync($"* {count} EXISTS");
                    await writer.WriteLineAsync("* 0 RECENT");
                    await writer.WriteLineAsync("* OK [UIDVALIDITY 1] Valid");
                    await writer.WriteLineAsync($"* OK [UIDNEXT {count + 1}] Next");
                    break;
                case "FETCH":
                    await WriteSummariesAsync(writer, arguments);
                    break;
                case "UID":
                    await WriteMessageAsync(writer, arguments);
                    break;
                case "CLOSE":
                    _selectedFolder = null;
                    break;
                case "LOGOUT":
                    await writer.WriteLineAsync("* BYE Closing");
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected IMAP command: {command}");
            }
        }

        private async Task WriteSummariesAsync(StreamWriter writer, string arguments)
        {
            string folder = _selectedFolder ?? throw new InvalidOperationException("No selected mailbox.");
            SummaryFolders.Enqueue(folder);
            Match range = Regex.Match(arguments, @"^(\d+):(\d+)");
            int first = int.Parse(range.Groups[1].Value);
            int last = int.Parse(range.Groups[2].Value);
            int count = folder == "INBOX" ? _messageCount : 2;
            StringBuilder summaries = new();
            for (int index = first; index <= Math.Min(last, count); index++)
            {
                int size = Encoding.ASCII.GetByteCount(Message(folder, index));
                summaries.Append($"* {index} FETCH (UID {index} INTERNALDATE \"01-Nov-2026 00:00:00 +0000\" RFC822.SIZE {size})\r\n");
            }
            await writer.WriteAsync(summaries.ToString());
        }

        private async Task WriteMessageAsync(StreamWriter writer, string arguments)
        {
            string folder = _selectedFolder ?? throw new InvalidOperationException("No selected mailbox.");
            Match uid = Regex.Match(arguments, @"^FETCH (\d+) ");
            int id = int.Parse(uid.Groups[1].Value);
            string message = Message(folder, id);
            Interlocked.Increment(ref _bodyRequests);
            await writer.WriteAsync($"* {id} FETCH (UID {id} BODY[] {{{Encoding.ASCII.GetByteCount(message)}}}\r\n{message})\r\n");
        }

        private static string Message(string folder, int id) =>
            $"From: sender@example.test\r\nTo: account@example.test\r\nSubject: {folder} message {id}\r\n"
            + $"Message-Id: <{folder}-{id}@example.test>\r\n\r\nBody of {folder} message {id}.\r\n";

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();
            _listener.Stop();
            await _server;
            _cancellation.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
