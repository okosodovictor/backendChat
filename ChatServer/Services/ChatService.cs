using System.Collections.Generic;
using ChatServer.Models;
using Grpc.Core;

namespace ChatServer.Services
{
    /// <summary>
    /// Accepts GRPC connections from clients and forwards them to their respective session.
    /// </summary>
    public class ChatService : ChatServer.ChatServerBase
    {
        private readonly ILogger _logger;

        private readonly Dictionary<string, List<User>> _chatRooms = new Dictionary<string, List<User>>();
        private readonly Dictionary<string, int> _tickCounts = new Dictionary<string, int>();
        private readonly Dictionary<string, DateTime> _tickTimer = new Dictionary<string, DateTime>();

        public class ConnectionLostException : Exception { }
        public ChatService(ILogger<ChatService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// This method is called by gRPC whenever a client establishes a connection.
        /// Each connected client will have its own call to this, with its own requestStream, responseStream and context.
        /// </summary>
        /// <param name="requestStream">You read from this stream to receive messages from the client.</param>
        /// <param name="responseStream">You write to this stream to send messages to the client.</param>
        /// <param name="context">Metadata, including CancellationToken, see https://docs.microsoft.com/en-us/aspnet/core/grpc/services?view=aspnetcore-6.0</param>
        /// <returns></returns>
        public override async Task Communicate(IAsyncStreamReader<ClientMessage> requestStream, IServerStreamWriter<ServerMessage> responseStream, ServerCallContext context)
        {
            string? userName = string.Empty;

            string? chatRoomId = string.Empty;

            while (true)
            {
                // For now, simply echo all messages back to the client         
                // You will want to implement the actual chat room logic here
                var clientMessage = await ReadMessageWithTimeoutAsync(requestStream, Timeout.InfiniteTimeSpan);
                switch (clientMessage.ContentCase)
                {
                    case ClientMessage.ContentOneofCase.Login:

                        var loginMessage = clientMessage.Login;

                        chatRoomId = loginMessage.ChatRoomId;

                        userName = loginMessage.UserName;

                        if (_chatRooms.Count > 0)
                        {
                            var user = _chatRooms[chatRoomId].Where(u => u.UserName == userName).FirstOrDefault();

                            if (user != null && user.IsLoggedIn == true)
                            {
                                await responseStream.WriteAsync(new ServerMessage
                                {
                                    LoginFailure = new ServerMessageLoginFailure { Reason = $"{clientMessage.Login.UserName}: is already login" }
                                });

                                break;
                            }
                        }

                        if (!ValidateUserName(userName))
                        {
                            var failureMessage = new ServerMessage
                            {
                                LoginFailure = new ServerMessageLoginFailure { Reason = "Invalid username" }
                            };

                            await responseStream.WriteAsync(failureMessage);
                            return;
                        }

                        await AddUserToChatRoom(chatRoomId, new User
                        {
                            UserName = userName,
                            streamWriter = responseStream,
                            IsLoggedIn = true
                        });

                        await BroadcastUserJoinedRoomMessage(userName, chatRoomId);

                        // Send login success message
                        var successMessage = new ServerMessage { LoginSuccess = new ServerMessageLoginSuccess() };

                        await responseStream.WriteAsync(successMessage);

                        //send system chat message
                        await SendChatAsync(responseStream, $"Received {clientMessage.ContentCase}");

                        break;

                    case ClientMessage.ContentOneofCase.Chat:

                        var chatMessage = clientMessage.Chat;

                        if (userName is not null && chatRoomId is not null)
                        {
                            await BroadcastMessageToChatRoom(chatRoomId, userName, chatMessage.Text);
                        }

                        if (chatMessage.Text.Contains("disconnecting", StringComparison.OrdinalIgnoreCase))
                        {
                            if (userName is not null && chatRoomId is not null)
                            {
                                RemoveUserFromChatRoom(chatRoomId, userName);

                                await BroadcastUserLeftRoomMessage(userName, chatRoomId);

                                if (_chatRooms.ContainsKey(chatRoomId) && _chatRooms[chatRoomId].Count == 0)
                                {
                                    _chatRooms.Remove(chatRoomId);

                                    _tickTimer.Remove(chatRoomId);
                                }
                            }
                        }

                        break;

                }
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="chatRoomId"></param>
        /// <param name="user"></param>
        /// <returns></returns>

        private async Task AddUserToChatRoom(string chatRoomId, User user)
        {
            if (!_chatRooms.ContainsKey(chatRoomId))
            {
                _chatRooms[chatRoomId] = new List<User> { user };

                _tickTimer[chatRoomId] = DateTime.UtcNow;

                await StartTickRoom(chatRoomId);
            }

            _chatRooms[chatRoomId].Add(user);
        }

        /// <summary>
        /// Read a single message from the client.
        /// </summary>
        /// <exception cref="ConnectionLostException"></exception>
        /// <exception cref="TimeoutException"></exception>
        private async Task<ClientMessage> ReadMessageWithTimeoutAsync(IAsyncStreamReader<ClientMessage> requestStream, TimeSpan timeout)
        {
            CancellationTokenSource cancellationTokenSource = new();

            CancellationToken cancellationToken = cancellationTokenSource.Token;

            cancellationTokenSource.CancelAfter(timeout);

            try
            {
                bool didMove = await requestStream.MoveNext(cancellationToken);

                if (didMove == false)
                {
                    throw new ConnectionLostException();
                }

                return requestStream.Current;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
            {
                throw new TimeoutException();
            }
        }

        /// <summary>
        /// Send a ServerMessageChat message to the client, using the provided text and username "SYSTEM".
        /// </summary>
        private async Task SendChatAsync(IServerStreamWriter<ServerMessage> responseStream, string text)
        {
            await responseStream.WriteAsync(new ServerMessage
            {
                Chat = new ServerMessageChat
                {
                    Text = text,
                    UserName = "SYSTEM"
                }
            });
        }

        /// <summary>
        /// </summary>
        /// <param name="userName"></param>
        /// <returns></returns>
        private bool ValidateUserName(string userName)
        {
            return !string.IsNullOrWhiteSpace(userName) && !userName.Contains(" ");
        }

        /// <summary>
        /// </summary>
        /// <param name="chatRoomId"></param>
        /// <param name="userName"></param>
        private void RemoveUserFromChatRoom(string chatRoomId, string userName)
        {
            if (_chatRooms.ContainsKey(chatRoomId))
            {
                var user = _chatRooms[chatRoomId].Where(u => u.UserName == userName).FirstOrDefault();

                if (user != null)
                {
                    _chatRooms[chatRoomId].Remove(user);
                }
                if (_chatRooms[chatRoomId].Count == 0)
                {
                    _chatRooms.Remove(chatRoomId);
                }
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="chatRoomId"></param>
        /// <param name="senderName"></param>
        /// <param name="text"></param>
        /// <returns></returns>
        private async Task BroadcastMessageToChatRoom(string chatRoomId, string senderName, string text)
        {
            if (_chatRooms.ContainsKey(chatRoomId))
            {
                var message = new ServerMessage { Chat = new ServerMessageChat { UserName = senderName, Text = text } };

                var tasks = new List<Task>();

                foreach (var userStream in _chatRooms[chatRoomId])
                {
                    //This senderName can be something of unique Id for each user.
                    if (userStream != null && userStream != default && userStream.UserName != senderName)
                    {
                        tasks.Add(userStream.streamWriter.WriteAsync(message));
                    }
                }

                await Task.WhenAll(tasks);
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="userName"></param>
        /// <param name="chatRoomId"></param>
        /// <returns></returns>
        private async Task BroadcastUserJoinedRoomMessage(string userName, string chatRoomId)
        {
            if (_chatRooms.ContainsKey(chatRoomId))
            {
                var message = new ServerMessage { UserJoined = new ServerMessageUserJoined { UserName = userName } };

                var tasks = new List<Task>();

                foreach (var userStream in _chatRooms[chatRoomId])
                {
                    if (userStream != null && userStream != default)
                    {
                        tasks.Add(userStream.streamWriter.WriteAsync(message));
                    }
                }

                await Task.WhenAll(tasks);
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="userName"></param>
        /// <param name="chatRoomId"></param>
        /// <returns></returns>
        private async Task BroadcastUserLeftRoomMessage(string userName, string chatRoomId)
        {
            if (_chatRooms.ContainsKey(chatRoomId))
            {
                var message = new ServerMessage { UserLeft = new ServerMessageUserLeft { UserName = userName } };

                var tasks = new List<Task>();

                foreach (var userStream in _chatRooms[chatRoomId])
                {
                    if (userStream != null && userStream != default)
                    {
                        tasks.Add(userStream.streamWriter.WriteAsync(message));
                    }
                }

                await Task.WhenAll(tasks);
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="chatRoomId"></param>
        /// <param name="message"></param>
        /// <returns></returns>
        private async Task BroadcastTickMessage(string chatRoomId, ServerMessage message)
        {
            if (_chatRooms.ContainsKey(chatRoomId))
            {
                var tasks = new List<Task>();

                foreach (var userStream in _chatRooms[chatRoomId])
                {
                    if (userStream != null && userStream != default)
                    {
                        tasks.Add(userStream.streamWriter.WriteAsync(message));
                    }
                }

                await Task.WhenAll(tasks);
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="chatRoomId"></param>
        /// <returns></returns>
        private async Task StartTickRoom(string chatRoomId)
        {
            int intitialTick = 0;

            while (_chatRooms.ContainsKey(chatRoomId))
            {
                var message = new ServerMessage { Tick = new ServerMessageTick { Tick = intitialTick++ } };

                await BroadcastTickMessage(chatRoomId, message);

                await Task.Delay(TimeSpan.FromSeconds(30));
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="chatRoomId"></param>
        /// <returns></returns>

        private async Task StopTickRoom(string chatRoomId)
        {
            if (_chatRooms.ContainsKey(chatRoomId))
            {
                _tickTimer.Remove(chatRoomId);

                var tickMessage = new ServerMessage { Tick = new ServerMessageTick { Tick = 0 } };

                await BroadcastTickMessage(chatRoomId, tickMessage);
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="chatRoomId"></param>
        /// <returns></returns>
        private async Task UpdateRoomTicks(string chatRoomId)
        {
            var tickMessage = new ServerMessage
            {
                Tick = new ServerMessageTick
                {
                    Tick = _tickCounts[chatRoomId]
                }
            };

            // Broadcast the tick message to all clients in the chat room
            await BroadcastTickMessage(chatRoomId, tickMessage);

            // Increase the tick count
            _tickCounts[chatRoomId]++;

            // delay 30s for the next tick
            await Task.Delay(TimeSpan.FromSeconds(30));
        }

        /// <summary>
        /// </summary>
        /// <returns></returns>
        private async Task UpdateRoomTicks()
        {
            while (true)
            {
                foreach (var chatRoomId in _chatRooms.Keys.ToList())
                {
                    if (_chatRooms.TryGetValue(chatRoomId, out var roomClients))
                    {
                        var tickMessage = new ServerMessage { Tick = new ServerMessageTick { Tick = _tickCounts[chatRoomId] } };

                        var tasks = new List<Task>();

                        foreach (var client in roomClients)
                        {
                            if (client != null && client != default)
                            {
                                tasks.Add(client.streamWriter.WriteAsync(tickMessage));
                            }
                        }

                        await Task.WhenAll(tasks);

                        _tickCounts[chatRoomId]++;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(30));
            }
        }
    }
}