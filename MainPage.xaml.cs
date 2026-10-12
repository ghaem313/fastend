using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;

namespace SecureTransferMobile;

public partial class MainPage : ContentPage
{
    private string filePath = "";
    const int PORT = 5001;

    public MainPage()
    {
        InitializeComponent();
    }

    private void TabSend_Clicked(object sender, EventArgs e)
    {
        ViewSend.IsVisible = true; ViewRecv.IsVisible = false;
        BtnTabSend.BackgroundColor = Color.FromArgb("#007acc"); BtnTabSend.TextColor = Colors.White;
        BtnTabRecv.BackgroundColor = Color.FromArgb("#1c1a2a"); BtnTabRecv.TextColor = Colors.Gray;
    }

    private void TabRecv_Clicked(object sender, EventArgs e)
    {
        ViewSend.IsVisible = false; ViewRecv.IsVisible = true;
        BtnTabRecv.BackgroundColor = Color.FromArgb("#007acc"); BtnTabRecv.TextColor = Colors.White;
        BtnTabSend.BackgroundColor = Color.FromArgb("#1c1a2a"); BtnTabSend.TextColor = Colors.Gray;
    }

    private void LogSend(string msg) => MainThread.BeginInvokeOnMainThread(() => TxtSendLog.Text += $"[{DateTime.Now:HH:mm:ss}] {msg}\n");
    private void LogRecv(string msg) => MainThread.BeginInvokeOnMainThread(() => TxtRecvLog.Text += $"[{DateTime.Now:HH:mm:ss}] {msg}\n");

    private string GetLocalIP()
    {
        foreach (var netInterface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            foreach (var addrInfo in netInterface.GetIPProperties().UnicastAddresses)
                if (addrInfo.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addrInfo.Address))
                    return addrInfo.Address.ToString();
        return "127.0.0.1";
    }

    private async void BtnSelectFile_Clicked(object sender, EventArgs e)
    {
        var result = await FilePicker.Default.PickAsync();
        if (result != null)
        {
            filePath = result.FullPath;
            LblSelectedFile.Text = result.FileName;
        }
    }

    private string FormatBytes(long bytes)
    {
        string[] suf = { "B", "KB", "MB", "GB" };
        if (bytes == 0) return "0 B";
        int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
        return $"{Math.Round(bytes / Math.Pow(1024, place), 1)} {suf[place]}";
    }

    private void UpdateProgress(long current, long total, ProgressBar pb, Label lbl)
    {
        if (total == 0) return;
        double progress = (double)current / total;
        int percentage = (int)(progress * 100);
        MainThread.BeginInvokeOnMainThread(() => {
            pb.Progress = progress;
            lbl.Text = $"درصد: {percentage}% ({FormatBytes(current)} از {FormatBytes(total)})";
        });
    }

    private async Task ReadExactAsync(Stream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count) {
            int read = await stream.ReadAsync(buffer, offset, count - offset);
            if (read == 0) throw new Exception("اتصال قطع شد.");
            offset += read;
        }
    }

    private async void BtnStartRecv_Clicked(object sender, EventArgs e)
    {
        BtnStartRecv.IsEnabled = false;
        try
        {
            using Aes aes = Aes.Create();
            aes.KeySize = 256; aes.GenerateKey(); aes.GenerateIV();
            TxtCodeOut.Text = Convert.ToBase64String(aes.Key) + ":" + Convert.ToBase64String(aes.IV);
            
            LogRecv($"آی‌پی موبایل شما: {GetLocalIP()}");
            LogRecv("در انتظار اتصال فرستنده...");

            TcpListener listener = new TcpListener(IPAddress.Any, PORT);
            listener.Start();

            using TcpClient client = await listener.AcceptTcpClientAsync();
            using NetworkStream netStream = client.GetStream();
            LogRecv("[+] متصل شد! در حال دریافت...");

            byte[] lenBytes = new byte[4];
            await ReadExactAsync(netStream, lenBytes, 4);
            int nameLen = BitConverter.ToInt32(lenBytes, 0);
            
            byte[] nameBytes = new byte[nameLen];
            await ReadExactAsync(netStream, nameBytes, nameLen);
            string fileName = Encoding.UTF8.GetString(nameBytes);

            byte[] sizeBytes = new byte[8];
            await ReadExactAsync(netStream, sizeBytes, 8);
            long totalSize = BitConverter.ToInt64(sizeBytes, 0);

            // در اندروید فایل ابتدا در حافظه کش موبایل دانلود می‌شود
            string cachePath = Path.Combine(FileSystem.CacheDirectory, fileName);
            byte[] buffer = new byte[1048576]; 
            int read; long totalRead = 0;

            using (FileStream fs = new FileStream(cachePath, FileMode.Create))
            using (CryptoStream cryptoStream = new CryptoStream(netStream, aes.CreateDecryptor(), CryptoStreamMode.Read))
            using (GZipStream gzipStream = new GZipStream(cryptoStream, CompressionMode.Decompress))
            {
                while ((read = await gzipStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fs.WriteAsync(buffer, 0, read);
                    totalRead += read;
                    if (totalRead % 2097152 < buffer.Length) // آپدیت رابط کاربری هر ۲ مگابایت
                        UpdateProgress(totalRead, totalSize, PbRecv, LblRecvStatus);
                }
            }
            UpdateProgress(totalSize, totalSize, PbRecv, LblRecvStatus);
            LogRecv("[✔] دانلود تمام شد.");
            
            // باز کردن پنجره Share اندروید برای ذخیره فایل در گالری یا مدیریت فایل
            await Share.Default.RequestAsync(new ShareFileRequest { Title = "ذخیره فایل دریافتی", File = new ShareFile(cachePath) });
            listener.Stop();
        }
        catch (Exception ex) { LogRecv($"خطا: {ex.Message}"); }
        finally { BtnStartRecv.IsEnabled = true; }
    }

    private async void BtnSend_Clicked(object sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(TxtSendIP.Text) || string.IsNullOrEmpty(TxtSendCode.Text)) {
            LogSend("لطفا فایل، آی‌پی و کد را وارد کنید."); return;
        }

        string[] parts = TxtSendCode.Text.Split(':');
        try
        {
            using Aes aes = Aes.Create();
            aes.Key = Convert.FromBase64String(parts[0]);
            aes.IV = Convert.FromBase64String(parts[1]);

            using TcpClient client = new TcpClient();
            LogSend("در حال اتصال به گیرنده...");
            await client.ConnectAsync(TxtSendIP.Text, PORT);
            
            using NetworkStream netStream = client.GetStream();
            string fileName = Path.GetFileName(filePath);
            long totalSize = new FileInfo(filePath).Length;

            byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);
            await netStream.WriteAsync(BitConverter.GetBytes(nameBytes.Length));
            await netStream.WriteAsync(nameBytes);
            await netStream.WriteAsync(BitConverter.GetBytes(totalSize));

            byte[] buffer = new byte[1048576];
            int read; long totalRead = 0;

            LogSend("در حال رمزنگاری و ارسال آنلاین...");
            using (CryptoStream cryptoStream = new CryptoStream(netStream, aes.CreateEncryptor(), CryptoStreamMode.Write))
            using (GZipStream gzipStream = new GZipStream(cryptoStream, CompressionLevel.Fastest))
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                while ((read = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await gzipStream.WriteAsync(buffer, 0, read);
                    totalRead += read;
                    if (totalRead % 2097152 < buffer.Length)
                        UpdateProgress(totalRead, totalSize, PbSend, LblSendStatus);
                }
            }
            UpdateProgress(100, 100, PbSend, LblSendStatus);
            LogSend("[✔] ارسال امن فایل با موفقیت پایان یافت.");
        }
        catch (Exception ex) { LogSend($"خطا: {ex.Message}"); }
    }
}
