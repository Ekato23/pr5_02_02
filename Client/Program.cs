using Common;
using Newtonsoft.Json;
using System.Net;
using System.Net.Sockets;
using System.Text;

internal class Program
{
    public static IPAddress IpAdress;
    public static int Port;
    public static int Id = -1;
    public static string Code = "";
    public static DateTime ConnectTime;
    public static bool IsConnected = false;

    private static Socket _socket;
    private static readonly object _sendLock = new object();

    private static void ResetSocket()
    {
        try { _socket?.Close(); } catch { }
        _socket = null;
    }

    private static bool EnsureConnected()
    {
        if (_socket != null && _socket.Connected)
            return true;

        ResetSocket();

        try
        {
            IPEndPoint endPoint = new IPEndPoint(IpAdress, Port);
            _socket = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Stream,
                ProtocolType.Tcp);

            _socket.ReceiveTimeout = 15000;
            _socket.SendTimeout = 15000;

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

    public static ViewModelMessage SendCommand(string command)
    {
        try
        {
            if (!EnsureConnected())
            {
                IsConnected = false;
                return null;
            }

            string message = command;
            if (!string.IsNullOrEmpty(Code) && command != "connect")
                message = command + " " + Code;

            ViewModelSend viewModelSend = new ViewModelSend(message, Id);
            byte[] messageByte = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(viewModelSend));

            lock (_sendLock)
            {
                _socket.Send(messageByte);

                byte[] bytes = new byte[10485760];
                int BytesRec = _socket.Receive(bytes);
                if (BytesRec == 0)
                {
                    ResetSocket();
                    if (command != "connect")
                        IsConnected = false;
                    return null;
                }

                string messageServer = Encoding.UTF8.GetString(bytes, 0, BytesRec);
                ViewModelMessage viewModelMessage = JsonConvert.DeserializeObject<ViewModelMessage>(messageServer);

                if (viewModelMessage == null)
                    return null;

                if (viewModelMessage.Command == "code")
                {
                    Code = viewModelMessage.Data;
                    ConnectTime = DateTime.Now;
                    IsConnected = true;

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("\nПодключение установлено");
                    Console.WriteLine($"Дата и время подключения: {ConnectTime:dd.MM.yyyy HH:mm:ss}");
                    Console.WriteLine($"IP-адрес клиента:          {IpAdress}");
                    Console.WriteLine($"Порт клиента:              {Port}");
                    Console.WriteLine($"Уникальный код:            {Code}");
                }
                else if (viewModelMessage.Command == "info")
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    string[] parts = viewModelMessage.Data.Split('|');
                    if (parts.Length >= 5)
                    {
                        Console.WriteLine($"[Обновление] Код: {parts[4]} | Время: {parts[0]} | Продолжительность: {parts[1]} | IP: {parts[2]} | Порт: {parts[3]}");
                    }
                    else
                    {
                        Console.WriteLine($"[Обновление] {viewModelMessage.Data}");
                    }
                }
                else if (viewModelMessage.Command == "message")
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Сервер: {viewModelMessage.Data}");

                    if (viewModelMessage.Data.Contains("нехватка мест") ||
                        viewModelMessage.Data.Contains("клиент отключен"))
                    {
                        IsConnected = false;
                    }
                }
                else if (viewModelMessage.Command == "disconnect")
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Сервер: {viewModelMessage.Data}");
                    IsConnected = false;
                }
                else if (viewModelMessage.Command == "pong")
                {
                }
                else if (viewModelMessage.Command == "error")
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Ошибка: {viewModelMessage.Data}");
                    IsConnected = false;
                }

                return viewModelMessage;
            }
        }
        catch (SocketException sexp)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Сетевая ошибка: " + sexp.Message);
            ResetSocket();
            return null;
        }
        catch (Exception exp)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Что-то случилось: " + exp.Message);
            ResetSocket();
            return null;
        }
    }

    public static void StartBackgroundTasks()
    {
        Task.Run(async () =>
        {
            while (IsConnected)
            {
                await Task.Delay(5000);
                if (!IsConnected) break;

                SendCommand("refresh");
                await Task.Delay(1000);

                if (!IsConnected) break;
                SendCommand("check");
            }

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Фоновые запросы остановлены.");
        });
    }

    public static long DurationSeconds()
    {
        return (long)(DateTime.Now - ConnectTime).TotalSeconds;
    }

    static void Main(string[] args)
    {
        Console.Write("Введите IP адрес сервера (клиент): ");
        string sIpAdress = Console.ReadLine();
        Console.Write("Введите порт: ");
        string sPort = Console.ReadLine();

        if (!int.TryParse(sPort, out Port) || !IPAddress.TryParse(sIpAdress, out IpAdress))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Некорректный IP или порт.");
            return;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Данные успешно введены. Подключаюсь к серверу.");

        SendCommand("connect");

        if (!IsConnected)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Не удалось подключиться. Завершение.");
            Console.Read();
            return;
        }

        StartBackgroundTasks();

        while (true)
        {
            Console.WriteLine("\nДоступные команды: info | exit");
            Console.Write("> ");
            string cmd = Console.ReadLine()?.Trim().ToLower();

            if (cmd == "exit") break;

            if (cmd == "info")
            {
                Console.WriteLine($"Дата и время подключения: {ConnectTime:dd.MM.yyyy HH:mm:ss}");
                Console.WriteLine($"Продолжительность: {DurationSeconds()} сек.");
                Console.WriteLine($"IP-адрес клиента: {IpAdress}");
                Console.WriteLine($"Порт клиента: {Port}");
                Console.WriteLine($"Уникальный код: {Code}");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Неизвестная команда. Используйте: info | exit");
            }
        }

        ResetSocket();
    }
}