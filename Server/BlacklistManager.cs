using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Server
{
    public static class BlacklistManager
    {
        private static readonly HashSet<string> _blacklist = new HashSet<string>();
        private static readonly object _lock = new object();

        // Добавить IP-адрес в чёрный список
        public static bool Add(string ip)
        {
            lock (_lock)
            {
                return _blacklist.Add(ip);
            }
        }

        // Удалить IP-адрес из чёрного списка
        public static bool Remove(string ip)
        {
            lock (_lock)
            {
                return _blacklist.Remove(ip);
            }
        }

        // Проверить, находится ли IP-адрес в чёрном списке
        public static bool Contains(string ip)
        {
            lock (_lock)
            {
                return _blacklist.Contains(ip);
            }
        }

        // Получить копию текущего чёрного списка
        public static List<string> GetAll()
        {
            lock (_lock)
            {
                return new List<string>(_blacklist);
            }
        }
    }
}
