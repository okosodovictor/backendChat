using System;
using Grpc.Core;

namespace ChatServer.Models
{
	public class User
	{
		public IServerStreamWriter<ServerMessage> streamWriter { get; set; }
		public string UserName { get; set; }
		public bool IsLoggedIn { get; set; }
	}
}

