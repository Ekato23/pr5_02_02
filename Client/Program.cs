using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Common;
using Newtonsoft.Json;

namespace Client
{
    internal class Program
    {
        public static IPAddress IpAdress;
        public static int Port;
        public static string Code = "";
        public static DateTime ConnectTime;
        public static bool IsConnected = false;

        private static Socket _socket;
        private static readonly object _sendLock = new object();
        private static bool _needReconnect = false;

        private static void ResetSocket()
        {
            try { _socket?.Shutdown(SocketShutdown.Both); } catch { }
            try { _socket?.Close(); } catch { }
            _socket = null;
        }

        private static bool EnsureConnected()
        {
            if (_socket != null && _socket.Connected)
                return true;

            ResetSocket();
            _needReconnect = true;

            try
            {
                IPEndPoint endPoint = new IPEndPoint(IpAdress, Port);
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                _socket.ReceiveTimeout = 10000;
                _socket.SendTimeout = 10000;
                _socket.Connect(endPoint);
                return _socket.Connected;
            }
            catch (Exception exp)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Не удалось подключиться: " + exp.Message);
                ResetSocket();
                return false;
            }
        }
        private static string ReceiveFullMessage(Socket socket)
        {

            byte[] lengthBuffer = new byte[4];
            int receivedLen = 0;
            while (receivedLen < 4)
            {
                int n = socket.Receive(lengthBuffer, receivedLen, 4 - receivedLen, SocketFlags.None);
                if (n == 0) throw new Exception("Соединение разорвано при чтении длины.");
                receivedLen += n;
            }

            int messageLength = BitConverter.ToInt32(lengthBuffer, 0);

            byte[] dataBuffer = new byte[messageLength];
            int totalReceived = 0;
            while (totalReceived < messageLength)
            {
                int n = socket.Receive(dataBuffer, totalReceived, messageLength - totalReceived, SocketFlags.None);
                if (n == 0) throw new Exception("Соединение разорвано при чтении данных.");
                totalReceived += n;
            }

            return Encoding.UTF8.GetString(dataBuffer);
        }

        public static ViewModelMessage SendCommand(string command)
        {
            try
            {
                if (!EnsureConnected())
                {
                    IsConnected = false;
                    return null;
                }

                if (_needReconnect)
                {
                    Code = "";
                    _needReconnect = false;
                    command = "connect";
                }

                string messagePayload = command;
                if (!string.IsNullOrEmpty(Code) && command != "connect")
                {
                    messagePayload = $"{command} {Code}";
                }

                var requestMsg = new ViewModelMessage(messagePayload, "");
                string json = JsonConvert.SerializeObject(requestMsg);
                byte[] jsonBytes = Encoding.UTF8.GetBytes(json);

                // Добавляем длину сообщения перед самим сообщением (4 байта)
                byte[] lengthBytes = BitConverter.GetBytes(jsonBytes.Length);
                byte[] finalPacket = new byte[4 + jsonBytes.Length];
                Buffer.BlockCopy(lengthBytes, 0, finalPacket, 0, 4);
                Buffer.BlockCopy(jsonBytes, 0, finalPacket, 4, jsonBytes.Length);

                lock (_sendLock)
                {
                    _socket.Send(finalPacket);

                    // Используем надежный прием
                    string responseJson = ReceiveFullMessage(_socket);

                    ViewModelMessage viewModelMessage = JsonConvert.DeserializeObject<ViewModelMessage>(responseJson);

                    if (viewModelMessage == null) return null;

                    if (viewModelMessage.Command == "code")
                    {
                        Code = viewModelMessage.Data;
                        IsConnected = true;

                        if (command == "connect")
                        {
                            ConnectTime = DateTime.Now;
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine("\nПодключение установлено");
                            Console.WriteLine($"Дата и время: {ConnectTime:dd.MM.yyyy HH:mm:ss}");
                            Console.WriteLine($"IP-адрес:     {IpAdress}");
                            Console.WriteLine($"Порт:         {Port}");
                            Console.WriteLine($"Код доступа:  {Code}");
                            Console.ResetColor();
                        }
                    }
                    else if (viewModelMessage.Command == "info")
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        string infoData = viewModelMessage.Data;
                        string[] parts = infoData.Split('|');
                        if (parts.Length >= 5)
                        {
                            Console.WriteLine($"[Refresh] Код: {parts[4]} | Старт: {parts[0]} | Длит.: {parts[1]} | IP: {parts[2]}:{parts[3]}");
                        }
                        else
                        {
                            Console.WriteLine($"[Info] {infoData}");
                        }
                        Console.ResetColor();
                    }
                    else if (viewModelMessage.Command == "disconnect")
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"Сервер отключил: {viewModelMessage.Data}");
                        Console.ResetColor();
                        IsConnected = false;
                        Code = "";
                        _needReconnect = true;
                        ResetSocket();
                    }
                    else if (viewModelMessage.Command == "error")
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"Ошибка сервера: {viewModelMessage.Data}");
                        Console.ResetColor();
                    }

                    return viewModelMessage;
                }
            }
            catch (Exception exp)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Ошибка связи: " + exp.Message);
                Console.ResetColor();
                _needReconnect = true;
                ResetSocket();
                return null;
            }
        }

        public static void StartBackgroundTasks()
        {
            Task.Run(async () =>
            {
                Console.WriteLine("Запущены фоновые задачи поддержки соединения...");
                while (IsConnected)
                {
                    await Task.Delay(5000);
                    if (!IsConnected) break;

                    var resRefresh = SendCommand("refresh");
                    if (!IsConnected) break;

                    await Task.Delay(2000);
                    if (!IsConnected) break;

                    var resCheck = SendCommand("check");
                    if (!IsConnected) break;
                }

                if (!IsConnected)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Фоновые задачи остановлены.");
                    Console.ResetColor();
                }
            });
        }

        public static long DurationSeconds()
        {
            return (long)(DateTime.Now - ConnectTime).TotalSeconds;
        }

        static void Main(string[] args)
        {
            Console.WriteLine("Клиент менеджера лицензий");

            Console.Write("Введите IP адрес сервера: ");
            string sIpAdress = Console.ReadLine();

            Console.Write("Введите порт: ");
            string sPort = Console.ReadLine();

            if (!int.TryParse(sPort, out Port) || !IPAddress.TryParse(sIpAdress, out IpAdress))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Некорректный IP или порт.");
                Console.Read();
                return;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Подключаюсь к серверу...");
            Console.ResetColor();

            SendCommand("connect");

            if (!IsConnected)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Не удалось подключиться.");
                Console.ResetColor();
                Console.Read();
                return;
            }

            StartBackgroundTasks();

            while (true)
            {
                Console.WriteLine("\nДоступные команды: info | exit");
                Console.Write("> ");
                string cmd = Console.ReadLine()?.Trim().ToLower();

                if (cmd == "exit")
                {
                    ResetSocket();
                    break;
                }

                if (cmd == "info")
                {
                    Console.WriteLine($"Дата подключения:   {ConnectTime:dd.MM.yyyy HH:mm:ss}");
                    Console.WriteLine($"Длительность:       {DurationSeconds()} сек.");
                    Console.WriteLine($"IP-адрес сервера:   {IpAdress}");
                    Console.WriteLine($"Порт сервера:       {Port}");
                    Console.WriteLine($"Уникальный код:     {Code}");
                    Console.WriteLine($"Статус:             {(IsConnected ? "Подключен" : "Отключен")}");
                }
                else if (cmd == "connect")
                {
                    Console.WriteLine("Запрос переподключения...");
                    _needReconnect = true;
                    SendCommand("connect");
                }
            }
        }
    }
}