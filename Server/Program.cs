using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Common;
using Newtonsoft.Json;

namespace Server
{
    class Program
    {
        // Глобальные настройки
        public static IPAddress IpAddress;
        public static int Port;
        public static int MaxLicenses;
        public static int DisconnectTimeMinutes;
        public static int ConfirmationTimeMinutes;

        public static List<ClientSession> ActiveSessions = new List<ClientSession>();
        private static readonly object _lock = new object();

        // Запрос настроек при запуске
        static void Main(string[] args)
        {
            Console.WriteLine("Настройка сервера");
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

        // Запуск сервера и прослушивание порта
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
                        ViewModelMessage banMsg = new ViewModelMessage("error", "Доступ запрещён. Ваш IP-адрес находится в чёрном списке.");
                        SendMessage(handler, banMsg);
                        handler.Close();

                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"[БЛОК] Отказано в подключении {session.ClientIp} (в чёрном списке).");
                        Console.ResetColor();
                        continue;
                    }
                    lock (_lock)
                    {
                        CleanupSessions();

                        if (ActiveSessions.Count >= MaxLicenses)
                        {
                            ViewModelMessage errorMsg = new ViewModelMessage("error", "Нет свободных лицензий. Сервер переполнен.");
                            SendMessage(handler, errorMsg);
                            handler.Close();
                            continue;
                        }

                        session.UniqueCode = Guid.NewGuid().ToString();
                        session.IsLicensed = true;
                        ActiveSessions.Add(session);
                    }

                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"\n[ПОДКЛЮЧЕНИЕ] Клиент {session.ClientIp}:{session.ClientPort}");
                    Console.WriteLine($"Уникальный код: {session.UniqueCode}");
                    Console.WriteLine($"Дата/Время: {session.ConnectTime}");
                    Console.WriteLine($"Всего клиентов: {ActiveSessions.Count}");
                    Console.ResetColor();

                    ViewModelMessage successMsg = new ViewModelMessage("code", session.UniqueCode);
                    SendMessage(handler, successMsg);

                    Thread clientThread = new Thread(() => ClientHandler(session));
                    clientThread.IsBackground = true;
                    clientThread.Start();
                }
            }
            catch (Exception exp)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Что-то случилось: " + exp.Message);
                Console.ResetColor();
            }
        }

        // Обработчик конкретного клиента
        private static void ClientHandler(ClientSession session)
        {
            try
            {
                while (true)
                {
                    // Таймауты
                    if ((DateTime.Now - session.LastConfirmationTime).TotalMinutes > ConfirmationTimeMinutes)
                    {
                        DisconnectClient(session, "Время подтверждения истекло. Требуется переподключение.");
                        break;
                    }

                    if ((DateTime.Now - session.ConnectTime).TotalMinutes > DisconnectTimeMinutes)
                    {
                        DisconnectClient(session, "Время сессии истекло. Требуется обновление кода.");
                        break;
                    }

                    if (session.Socket.Available > 0)
                    {
                        byte[] buffer = new byte[1048576];
                        int bytesRec;
                        StringBuilder sb = new StringBuilder();

                        try
                        {
                            do
                            {
                                bytesRec = session.Socket.Receive(buffer);
                                if (bytesRec == 0) break;
                                sb.Append(Encoding.UTF8.GetString(buffer, 0, bytesRec));
                            }
                            while (session.Socket.Available > 0);
                        }
                        catch (SocketException)
                        {
                            break;
                        }

                        if (sb.Length == 0) break;

                        string data = sb.ToString();
                        session.LastConfirmationTime = DateTime.Now;

                        ViewModelMessage msg = null;
                        try
                        {
                            msg = JsonConvert.DeserializeObject<ViewModelMessage>(data);
                        }
                        catch (JsonException)
                        {
                            msg = null;
                        }

                        if (msg != null && msg.Command != null)
                        {
                            string[] parts = msg.Command.Split(' ');
                            string actualCommand = parts[0].Trim().ToLower();

                            if (parts.Length > 1)
                            {
                                string clientCode = parts[1].Trim();
                                if (clientCode != session.UniqueCode)
                                {
                                    DisconnectClient(session, "Неверный уникальный код.");
                                    break;
                                }
                            }

                            if (actualCommand == "check")
                            {
                                if (BlacklistManager.Contains(session.ClientIp.ToString()))
                                {
                                    DisconnectClient(session, "Ваш IP-адрес заблокирован администратором.");
                                    break;
                                }
                                ViewModelMessage reply = new ViewModelMessage("info", "OK");
                                SendMessage(session.Socket, reply);
                            }
                            else if (actualCommand == "refresh")
                            {
                                string info = $"{session.ConnectTime:dd.MM.yyyy HH:mm:ss}|" +
                                              $"{(DateTime.Now - session.ConnectTime).ToString(@"hh\:mm\:ss")}|" +
                                              $"{session.ClientIp}|{session.ClientPort}|{session.UniqueCode}";
                                ViewModelMessage reply = new ViewModelMessage("info", info);
                                SendMessage(session.Socket, reply);
                            }
                            else if (actualCommand == "ping")
                            {
                                ViewModelMessage reply = new ViewModelMessage("pong", "OK");
                                SendMessage(session.Socket, reply);
                            }
                            else if (actualCommand == "connect")
                            {
                                ViewModelMessage reply = new ViewModelMessage("code", session.UniqueCode);
                                SendMessage(session.Socket, reply);
                            }
                        }
                    }
                    else
                    {
                        try
                        {
                            if (session.Socket.Poll(1000, SelectMode.SelectRead) && session.Socket.Available == 0)
                            {
                                break;
                            }
                        }
                        catch (SocketException)
                        {
                            break;
                        }

                        Thread.Sleep(50);
                    }
                }
            }
            catch (Exception)
            {
                // Клиент отключился аварийно
            }
            finally
            {
                lock (_lock)
                {
                    ActiveSessions.Remove(session);
                }
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[ОТКЛЮЧЕНИЕ] Клиент {session.ClientIp} отключен.");
                Console.ResetColor();
            }
        }
      
        // Отправка сообщения клиенту
        private static void SendMessage(Socket socket, ViewModelMessage msg)
        {
            try
            {
                string reply = JsonConvert.SerializeObject(msg);
                byte[] message = Encoding.UTF8.GetBytes(reply);
                socket.Send(message);
            }
            catch { }
        }

        // Принудительное отключение клиента
        private static void DisconnectClient(ClientSession session, string reason)
        {
            try
            {
                ViewModelMessage msg = new ViewModelMessage("disconnect", reason);
                SendMessage(session.Socket, msg);
                session.Socket.Close();
            }
            catch { }
        }

        // Очистка мертвых сессий
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
                    DisconnectClient(s, "Ваш IP-адрес заблокирован администратором.");
                    ActiveSessions.Remove(s);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[АДМИН] Клиент {ip} (код {s.UniqueCode}) отключен за бан.");
                    Console.ResetColor();
                }
            }
        }

        // Обработчик консольных команд
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
                            DisconnectClient(sessionToKick, "Принудительное отключение администратором.");
                            ActiveSessions.Remove(sessionToKick);
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"[АДМИН] Клиент {codeToKick} отключен.");
                            Console.ResetColor();
                        }
                        else
                        {
                            Console.WriteLine("Клиент с таким кодом не найден.");
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
                            Console.WriteLine($"Код: {s.UniqueCode} | IP: {s.ClientIp} | Время: {s.ConnectTime}");
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
                    else
                    {
                        Console.WriteLine($"IP {ip} уже находится в чёрном списке.");
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
                    else
                    {
                        Console.WriteLine($"IP {ip} не найден в чёрном списке.");
                    }
                }
                else if (cmd == "blacklist")
                {
                    List<string> banned = BlacklistManager.GetAll();
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"\nЧёрный список ({banned.Count}):");
                    foreach (var ip in banned)
                    {
                        Console.WriteLine($" - {ip}");
                    }
                    Console.ResetColor();
                }
                else
                {
                    Console.WriteLine("Доступные команды: list, kick <код>, ban <ip>, unban <ip>, blacklist");
                }
            }
        }
    }
}