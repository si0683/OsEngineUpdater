using System.Security.Cryptography;
using System.Text;

namespace OsEngineUpdater.Core
{
    /// <summary>
    /// Шифрование токенов для текущего пользователя Windows (DPAPI). Расшифровать может только он же
    /// (в том числе задание Планировщика, запущенное от его имени).
    /// </summary>
    public static class Secret
    {
        public static string Protect(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(data);
        }

        public static string Unprotect(string protectedValue)
        {
            if (string.IsNullOrEmpty(protectedValue))
            {
                return "";
            }

            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser));
            }
            catch
            {
                // зашифровано под другим пользователем / на другом компьютере
                return "";
            }
        }
    }
}
