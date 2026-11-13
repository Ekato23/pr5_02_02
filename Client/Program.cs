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

    // Отправка одной команды серверу и получение ответа
    public static ViewModelMessage SendCommand(string command)
    {
        try
        {
            IPEndPoint endPoint = new IPEndPoint(IpAdress, Port);
            using Socket socket = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Stream,
                ProtocolType.Tcp);

            socket.Connect(endPoint);

            if (!socket.Connected)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Подключение не удалось!");
                return null;
            }

            // Код передаём вторым словом, если он уже есть:
            // "connect" | "refresh <code>" | "check <code>"
            string message = command;
            if (!string.IsNullOrEmpty(Code))
                message = command + " " + Code;

            ViewModelSend viewModelSend = new ViewModelSend(message, Id);

            byte[] messageByte = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(viewModelSend));
            socket.Send(messageByte);

            byte[] bytes = new byte[10485760];
            int BytesRec = socket.Receive(bytes);
            string messageServer = Encoding.UTF8.GetString(bytes, 0, BytesRec);
            ViewModelMessage viewModelMessage = JsonConvert.DeserializeObject<ViewModelMessage>(messageServer);

            if (viewModelMessage == null)
                return null;

            // Обработка ответов сервера

            if (viewModelMessage.Command == "code")
            {
                // Первичное подключение: сервер вернул уникальный код
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
                // Обновление данных о подключении 
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
            else if (viewModelMessage.Command == "error")
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Ошибка: {viewModelMessage.Data}");
                IsConnected = false;
            }

            return viewModelMessage;
        }
        catch (Exception exp)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Что-то случилось: " + exp.Message);
            return null;
        }
    }
    //Периодически обновляет код и проверяет блокировку
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

        // Получение уникального кода
        SendCommand("connect");

        if (!IsConnected)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Не удалось подключиться. Завершение.");
            Console.Read();
            return;
        }

        // Периодические запросы refresh/check
        StartBackgroundTasks();

        // Управление из консоли
        while (true)
        {
            Console.WriteLine("\nДоступные команды: info | refresh | check | exit");
            Console.Write("> ");
            string cmd = Console.ReadLine()?.Trim().ToLower();

            if (cmd == "exit") break;

            if (cmd == "info")
            {
                Console.WriteLine($"Дата и время подключения: {ConnectTime:dd.MM.yyyy HH:mm:ss}");
                Console.WriteLine($"Продолжительность: {(DateTime.Now - ConnectTime).ToString(@"hh\:mm\:ss")}");
                Console.WriteLine($"IP-адрес клиента: {IpAdress}");
                Console.WriteLine($"Порт клиента: {Port}");
                Console.WriteLine($"Уникальный код: {Code}");
            }
            else if (cmd == "refresh" || cmd == "check")
            {
                SendCommand(cmd);
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Неизвестная команда. Используйте: info | refresh | check | exit");
            }
        }
    }
}
