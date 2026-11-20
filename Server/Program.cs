using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Common;
using Newtonsoft.Json;

namespace Server
{
    class Program
    {
        public static IPAddress IpAddress;
        public static int Port;
        public static int MaxLicenses;
        public static int DisconnectTimeMinutes;
        public static int ConfirmationTimeMinutes;

        public static List<ClientSession> ActiveSessions = new List<ClientSession>();
        private static readonly object _lock = new object();

        static void Main(string[] args)
        {
            Console.WriteLine("Настройка сервера лицензий");

            Console.Write("Введите IP адрес сервера (например, 127.0.0.1): ");
            string sIpAddress = Console.ReadLine();
            while (!IPAddress.TryParse(sIpAddress, out IpAddress))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Некорректный IP адрес. Попробуйте снова: ");
                Console.ResetColor();
                sIpAddress = Console.ReadLine();
            }

            Console.Write("Введите порт сервера (например, 8888): ");
            string sPort = Console.ReadLine();
            while (!int.TryParse(sPort, out Port))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Некорректный порт. Попробуйте снова: ");
                Console.ResetColor();
                sPort = Console.ReadLine();
            }

            Console.Write("Введите максимальное количество клиентов (лицензий): ");
            string sMaxLic = Console.ReadLine();
            while (!int.TryParse(sMaxLic, out MaxLicenses))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Некорректное число. Попробуйте снова: ");
                Console.ResetColor();
                sMaxLic = Console.ReadLine();
            }

            Console.Write("Введите время отключения клиента (в минутах): ");
            string sDiscTime = Console.ReadLine();
            while (!int.TryParse(sDiscTime, out DisconnectTimeMinutes))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Некорректное число. Попробуйте снова: ");
                Console.ResetColor();
                sDiscTime = Console.ReadLine();
            }

            Console.Write("Введите время подтверждения кода (в минутах): ");
            string sConfTime = Console.ReadLine();
            while (!int.TryParse(sConfTime, out ConfirmationTimeMinutes))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Некорректное число. Попробуйте снова: ");
                Console.ResetColor();
                sConfTime = Console.ReadLine();
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\nНастройки сохранены. Запускаем сервер...");
            Console.ResetColor();

            StartServer();
        }

        public static void StartServer()
        {
            IPEndPoint endPoint = new IPEndPoint(IpAddress, Port);
            Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            try
            {
                listener.Bind(endPoint);
                listener.Listen(10);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Сервер запущен на {IpAddress}:{Port}");
                Console.ResetColor();

                Thread consoleThread = new Thread(ConsoleCommandHandler);
                consoleThread.IsBackground = true;
                consoleThread.Start();

                while (true)
                {
                    Socket handler = listener.Accept();

                    ClientSession session = new ClientSession();
                    session.Socket = handler;
                    session.ConnectTime = DateTime.Now;
                    session.LastConfirmationTime = DateTime.Now;
                    session.ClientIp = ((IPEndPoint)handler.RemoteEndPoint).Address;
                    session.ClientPort = ((IPEndPoint)handler.RemoteEndPoint).Port;

                    if (BlacklistManager.Contains(session.ClientIp.ToString()))
                    {
                        SendMessage(handler, new ViewModelMessage("error", "Доступ запрещён. Ваш IP-адрес находится в чёрном списке."));
                        handler.Close();
                        continue;
                    }

                    lock (_lock)
                    {
                        CleanupSessions();
                        if (ActiveSessions.Count >= MaxLicenses)
                        {
                            SendMessage(handler, new ViewModelMessage("error", "Нет свободных лицензий."));
                            handler.Close();
                            continue;
                        }

                        session.UniqueCode = Guid.NewGuid().ToString();
                        session.IsLicensed = true;
                        ActiveSessions.Add(session);
                    }

                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"\n[ПОДКЛЮЧЕНИЕ] Клиент {session.ClientIp}:{session.ClientPort}");
                    Console.WriteLine($"Код: {session.UniqueCode}");
                    Console.ResetColor();

                    SendMessage(handler, new ViewModelMessage("code", session.UniqueCode));

                    Thread clientThread = new Thread(() => ClientHandler(session));
                    clientThread.IsBackground = true;
                    clientThread.Start();
                }
            }
            catch (Exception exp)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Ошибка сервера: " + exp.Message);
                Console.ResetColor();
            }
        }

        private static void ClientHandler(ClientSession session)
        {
            try
            {
                session.Socket.ReceiveTimeout = 60000;

                while (true)
                {
                    if ((DateTime.Now - session.ConnectTime).TotalMinutes > DisconnectTimeMinutes)
                    {
                        DisconnectClient(session, "Время сессии истекло.");
                        break;
                    }

                    if ((DateTime.Now - session.LastConfirmationTime).TotalMinutes > ConfirmationTimeMinutes)
                    {
                        DisconnectClient(session, "Время подтверждения истекло.");
                        break;
                    }

                    byte[] lengthBuffer = new byte[4];
                    int receivedLen = 0;
                    while (receivedLen < 4)
                    {
                        int n = session.Socket.Receive(lengthBuffer, receivedLen, 4 - receivedLen, SocketFlags.None);
                        if (n == 0) break;
                        receivedLen += n;
                    }
                    if (receivedLen < 4) break;

                    int messageLength = BitConverter.ToInt32(lengthBuffer, 0);

                    byte[] dataBuffer = new byte[messageLength];
                    int totalReceived = 0;
                    while (totalReceived < messageLength)
                    {
                        int n = session.Socket.Receive(dataBuffer, totalReceived, messageLength - totalReceived, SocketFlags.None);
                        if (n == 0) break;
                        totalReceived += n;
                    }
                    if (totalReceived < messageLength) break;

                    string data = Encoding.UTF8.GetString(dataBuffer);
                    session.LastConfirmationTime = DateTime.Now;

                    ViewModelMessage msg = null;
                    try { msg = JsonConvert.DeserializeObject<ViewModelMessage>(data); }
                    catch { }

                    if (msg != null && msg.Command != null)
                    {
                        string[] parts = msg.Command.Split(' ');
                        string actualCommand = parts[0].Trim().ToLower();
                        string clientCode = parts.Length > 1 ? parts[1].Trim() : "";

                        if (!string.IsNullOrEmpty(clientCode) && clientCode != session.UniqueCode)
                        {
                            DisconnectClient(session, "Неверный код.");
                            break;
                        }

                        switch (actualCommand)
                        {
                            case "connect":
                                SendMessage(session.Socket, new ViewModelMessage("code", session.UniqueCode));
                                break;
                            case "check":
                                if (BlacklistManager.Contains(session.ClientIp.ToString()))
                                {
                                    DisconnectClient(session, "IP заблокирован.");
                                    break;
                                }
                                SendMessage(session.Socket, new ViewModelMessage("info", "OK"));
                                break;
                            case "refresh":
                                string info = $"{session.ConnectTime:dd.MM.yyyy HH:mm:ss}|" +
                                              $"{(DateTime.Now - session.ConnectTime).ToString(@"hh\:mm\:ss")}|" +
                                              $"{session.ClientIp}|{session.ClientPort}|{session.UniqueCode}";
                                SendMessage(session.Socket, new ViewModelMessage("info", info));
                                break;
                            case "ping":
                                SendMessage(session.Socket, new ViewModelMessage("pong", "OK"));
                                break;
                        }
                    }
                }
            }
            catch (Exception) { }
            finally
            {
                lock (_lock) { ActiveSessions.Remove(session); }
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[ОТКЛЮЧЕНИЕ] Клиент {session.ClientIp} отключен.");
                Console.ResetColor();
            }
        }
        private static void SendMessage(Socket socket, ViewModelMessage msg)
        {
            try
            {
                string json = JsonConvert.SerializeObject(msg);
                byte[] jsonBytes = Encoding.UTF8.GetBytes(json);

                byte[] lengthBytes = BitConverter.GetBytes(jsonBytes.Length);
                byte[] finalPacket = new byte[4 + jsonBytes.Length];

                Buffer.BlockCopy(lengthBytes, 0, finalPacket, 0, 4);
                Buffer.BlockCopy(jsonBytes, 0, finalPacket, 4, jsonBytes.Length);

                socket.Send(finalPacket);
            }
            catch { }
        }

        private static void DisconnectClient(ClientSession session, string reason)
        {
            try
            {
                SendMessage(session.Socket, new ViewModelMessage("disconnect", reason));
                session.Socket.Close();
            }
            catch { }
        }

        private static void CleanupSessions()
        {
            ActiveSessions.RemoveAll(s => (DateTime.Now - s.LastConfirmationTime).TotalMinutes > ConfirmationTimeMinutes + 1);
        }

        private static void KickByIp(string ip)
        {
            lock (_lock)
            {
                var sessionsToKick = ActiveSessions.Where(s => s.ClientIp.ToString() == ip).ToList();
                foreach (var s in sessionsToKick)
                {
                    DisconnectClient(s, "IP заблокирован администратором.");
                    ActiveSessions.Remove(s);
                }
            }
        }

        private static void ConsoleCommandHandler()
        {
            while (true)
            {
                string command = Console.ReadLine();
                if (string.IsNullOrEmpty(command)) continue;

                string[] parts = command.Split(' ');
                string cmd = parts[0].ToLower();

                if (cmd == "kick" && parts.Length > 1)
                {
                    string codeToKick = parts[1];
                    lock (_lock)
                    {
                        ClientSession sessionToKick = ActiveSessions.FirstOrDefault(s => s.UniqueCode == codeToKick);
                        if (sessionToKick != null)
                        {
                            DisconnectClient(sessionToKick, "Отключен администратором.");
                            ActiveSessions.Remove(sessionToKick);
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"[АДМИН] Клиент {codeToKick} отключен.");
                            Console.ResetColor();
                        }
                    }
                }
                else if (cmd == "list")
                {
                    lock (_lock)
                    {
                        Console.WriteLine($"\nАктивные клиенты ({ActiveSessions.Count}):");
                        foreach (var s in ActiveSessions)
                        {
                            Console.WriteLine($"Код: {s.UniqueCode} | IP: {s.ClientIp} | Порт: {s.ClientPort} | Время: {s.ConnectTime:HH:mm:ss}");
                        }
                    }
                }
                else if (cmd == "ban" && parts.Length > 1)
                {
                    string ip = parts[1];
                    if (BlacklistManager.Add(ip))
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"[АДМИН] IP {ip} добавлен в чёрный список.");
                        Console.ResetColor();
                        KickByIp(ip);
                    }
                }
                else if (cmd == "unban" && parts.Length > 1)
                {
                    string ip = parts[1];
                    if (BlacklistManager.Remove(ip))
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"[АДМИН] IP {ip} удалён из чёрного списка.");
                        Console.ResetColor();
                    }
                }
                else if (cmd == "blacklist")
                {
                    List<string> banned = BlacklistManager.GetAll();
                    Console.WriteLine($"\nЧёрный список ({banned.Count}):");
                    foreach (var ip in banned) Console.WriteLine($" - {ip}");
                }
            }
        }
    }
}