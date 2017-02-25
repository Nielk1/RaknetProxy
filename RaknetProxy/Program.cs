using System;
using System.Collections.Generic;
using System.Configuration;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RaknetProxy
{
    class Program
    {

        #region pInvoke

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public int showCmd;
            public Point ptMinPosition;
            public Point ptMaxPosition;
            public Rectangle rcNormalPosition;
        }

        private enum ShowWindowCommands
        {
            Hide = 0,
            Normal = 1,
            ShowMinimized = 2,
            Maximize = 3,
            ShowMaximized = 3,
            ShowNoActivate = 4,
            Show = 5,
            Minimize = 6,
            ShowMinNoActive = 7,
            ShowNA = 8,
            Restore = 9,
            ShowDefault = 10,
            ForceMinimize = 11
        }

        [DllImport("user32.dll")]
        private static extern bool EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);

        [DllImport("user32.dll")]
        private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

        private const uint SC_CLOSE = 0xF060;
        private const uint MF_ENABLED = 0x00000000;
        private const uint MF_DISABLED = 0x00000002;

        #endregion

        private static NotifyIcon Tray = default(NotifyIcon);
        private static IntPtr Me = default(IntPtr);

        private static List<GameServer> GetHosts;
        private static List<GameServer> PostHosts;
        private static int port;
        private static string hostname;
        private static int LastPostIndex = -1;

        private class GameServer
        {
            public string host;
            public int port;
            public int timeout;
        }

        static void Main(string[] args)
        {
            hostname = ConfigurationManager.AppSettings["hostname"];
            port = int.Parse(ConfigurationManager.AppSettings["port"]);

            GetHosts = new List<GameServer>();
            PostHosts = new List<GameServer>();

            for (int i = 1; ; i++)
            {
                try
                {
                    string host = ConfigurationManager.AppSettings[$"GetHost{i}"];
                    string port = ConfigurationManager.AppSettings[$"GetPort{i}"];
                    string timeout = ConfigurationManager.AppSettings[$"GetTimeout{i}"];

                    if (string.IsNullOrEmpty(host)) break;

                    GetHosts.Add(new GameServer()
                    {
                        host = host,
                        port = int.Parse(port),
                        timeout = int.Parse(timeout)
                    });
                }
                catch
                {
                    break;
                }
            }

            for (int i = 1; ; i++)
            {
                try
                {
                    string host = ConfigurationManager.AppSettings[$"PostHost{i}"];
                    string port = ConfigurationManager.AppSettings[$"PostPort{i}"];
                    string timeout = ConfigurationManager.AppSettings[$"PostTimeout{i}"];

                    if (string.IsNullOrEmpty(host)) break;

                    PostHosts.Add(new GameServer()
                    {
                        host = host,
                        port = int.Parse(port),
                        timeout = int.Parse(timeout)
                    });
                }
                catch
                {
                    break;
                }
            }

            Console.Title = "Raknet Proxy";

            // Get The Console Window Handle
            Me = GetConsoleWindow();

            // Disable Close Button (X)
            EnableMenuItem(GetSystemMenu(Me, false), SC_CLOSE, (uint)(MF_ENABLED | MF_DISABLED));

            MenuItem mExit = new MenuItem("Exit", new EventHandler(Exit));
            ContextMenu Menu = new ContextMenu(new MenuItem[] { mExit });

            Tray = new NotifyIcon()
            {
                Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location),
                Visible = true,
                Text = Console.Title,
                ContextMenu = Menu
            };
            Tray.DoubleClick += new EventHandler(DoubleClick);

            // Detect When The Console Window is Minimized and Hide it
            Task.Factory.StartNew(() =>
            {
                for (;;)
                {
                    WINDOWPLACEMENT wPlacement = new WINDOWPLACEMENT();
                    GetWindowPlacement(Me, ref wPlacement);
                    if (wPlacement.showCmd == (int)ShowWindowCommands.ShowMinimized)
                        ShowWindow(Me, (int)ShowWindowCommands.Hide);
                    // 1 ms Delay to Avoid High CPU Usage
                    //Wait(1);
                    Thread.Sleep(100);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            Task.Factory.StartNew(() =>
            {
                using (HttpListener listener = new HttpListener())
                {
                    listener.Prefixes.Add($"http://{hostname}:{port}/");
                    listener.Start();
                    for (;;)
                    {
                        var context = listener.GetContext();

                        switch (context.Request.HttpMethod)
                        {
                            case "GET":
                                GetGames(context);
                                break;
                            case "POST":
                            case "PUT":
                                PostGame(context, context.Request.HttpMethod);
                                break;
                            case "DELETE":
                                DeleteGame(context);
                                break;
                            default:
                                Console.WriteLine("Unknown HTTP Method");
                                context.Response.Headers.Add("Allow", "GET, POST, PUT, DELETE");
                                context.Response.StatusCode = 405;
                                //context.Response.ContentLength64 = 1;
                                //context.Response.OutputStream.WriteByte(0x00);
                                context.Response.OutputStream.Close();
                                break;
                        }
                    }
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            Console.CancelKeyPress += Console_CancelKeyPress;

            Application.Run();
        }

        private static void DeleteGame(HttpListenerContext context)
        {
            if (LastPostIndex > -1)
            {
                var host = PostHosts[LastPostIndex];
                var builder = new UriBuilder(context.Request.Url);
                builder.Host = host.host;
                builder.Port = host.port;
                string testUrl = builder.Uri.ToString();

                ConsoleColor tmpColX = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine($"Trying DELETE {testUrl}");
                Console.ForegroundColor = tmpColX;

                try
                {
                    HttpWebRequest request = (HttpWebRequest)HttpWebRequest.Create(testUrl);
                    request.Timeout = host.timeout;
                    request.Method = "DELETE";

                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    {
                        using (Stream responseStream = response.GetResponseStream())
                        {
                            responseStream.CopyTo(context.Response.OutputStream);
                            context.Response.OutputStream.Close();
                        }
                    }
                    ConsoleColor tmpCol = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("Success");
                    Console.ForegroundColor = tmpCol;
                    return;
                }
                catch (Exception ex)
                {
                    ConsoleColor tmpCol = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine(ex.ToString());
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Failed");
                    Console.ForegroundColor = tmpCol;
                }
            }
            LastPostIndex = -1;
            ConsoleColor tmpColZ = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("Target POST server unlocked");
            Console.ForegroundColor = tmpColZ;
        }

        private static void PostGame(HttpListenerContext context, string httpMethod)
        {
            if(LastPostIndex >= 0)
            {
                PostGame2(PostHosts[LastPostIndex], context, httpMethod);
                return;
            }
            for (int i = 0; i < PostHosts.Count; i++)
            {
                var host = PostHosts[i];
                if (PostGame2(host, context, httpMethod))
                {
                    LastPostIndex = i;
                    ConsoleColor tmpColZ = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.WriteLine($"Target POST server locked to \"{PostHosts[LastPostIndex].host}\"");
                    Console.ForegroundColor = tmpColZ;
                    return;
                }
            }
        }

        private static bool PostGame2(GameServer host, HttpListenerContext context, string httpMethod)
        {
            var builder = new UriBuilder(context.Request.Url);
            builder.Host = host.host;
            builder.Port = host.port;
            string testUrl = builder.Uri.ToString();

            ConsoleColor tmpColX = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"Trying {httpMethod} {testUrl}");
            Console.ForegroundColor = tmpColX;

            try
            {
                HttpWebRequest request = (HttpWebRequest)HttpWebRequest.Create(testUrl);
                request.Timeout = host.timeout;
                request.Method = httpMethod;
                request.ContentLength = context.Request.ContentLength64;
                Stream postData = request.GetRequestStream();
                context.Request.InputStream.CopyTo(postData);
                postData.Close();


                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    using (Stream responseStream = response.GetResponseStream())
                    {
                        responseStream.CopyTo(context.Response.OutputStream);
                        context.Response.OutputStream.Close();
                    }
                }
                ConsoleColor tmpCol = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Success");
                Console.ForegroundColor = tmpCol;
                return true;
            }
            catch (Exception ex)
            {
                ConsoleColor tmpCol = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine(ex.ToString());
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Failed");
                Console.ForegroundColor = tmpCol;
            }
            return false;
        }

        private static void GetGames(HttpListenerContext context)
        {
            foreach (var host in GetHosts)
            {
                var builder = new UriBuilder(context.Request.Url);
                builder.Host = host.host;
                builder.Port = host.port;
                string testUrl = builder.Uri.ToString();

                ConsoleColor tmpColX = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine($"Trying GET {testUrl}");
                Console.ForegroundColor = tmpColX;

                try
                {
                    HttpWebRequest request = (HttpWebRequest)HttpWebRequest.Create(testUrl);
                    request.Timeout = host.timeout;

                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    {
                        using (Stream responseStream = response.GetResponseStream())
                        {
                            responseStream.CopyTo(context.Response.OutputStream);
                            context.Response.OutputStream.Close();
                        }
                    }
                    ConsoleColor tmpCol = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("Success");
                    Console.ForegroundColor = tmpCol;
                    return;
                }
                catch (Exception ex)
                {
                    ConsoleColor tmpCol = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine(ex.ToString());
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Failed");
                    Console.ForegroundColor = tmpCol;
                }
            }
        }

        //private static long CopyStream(Stream input, Stream output)
        //{
        //    long length = 0;
        //    byte[] buffer = new byte[32768];
        //    int read;
        //    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        //    {
        //        output.Write(buffer, 0, read);
        //        length += read;
        //    }
        //    return length;
        //}

        private static void DoubleClick(object sender, EventArgs e)
        {
            ShowWindow(Me, (int)ShowWindowCommands.Restore);
        }

        private static void Exit(object sender, EventArgs e)
        {
            Tray.Dispose();
            Application.Exit();
        }

        private static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            Tray.Dispose();
            Application.Exit();
        }
    }
}