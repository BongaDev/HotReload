﻿using System;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISimpleHttpListener.Rx.Enum;
using ISimpleHttpListener.Rx.Model;
using SimpleHttpListener.Rx.Extension;
using SimpleHttpListener.Rx.Model;
using SimpleHttpListener.Rx.Service;
using System.Text;
using System.Net.Http;
using System.Collections.Concurrent;
using Xamarin.Forms.HotReload.Reloader;
using System.Linq;
using Xamarin.Forms.Xaml;
using System.Diagnostics;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Net.NetworkInformation;
using System.CodeDom.Compiler;
using System.Reflection;

namespace Xamarin.Forms
{
    public sealed class HotReloader
    {
        private static readonly Lazy<HotReloader> _lazyHotReloader;

        static HotReloader() => _lazyHotReloader = new Lazy<HotReloader>(() => new HotReloader());

        public static HotReloader Current => _lazyHotReloader.Value;

        private IDisposable _subscription;
        private CancellationTokenSource _reloaderCancellationTokenSource;
        private ConcurrentDictionary<string, ReloadItem> _fileMapping;

        private HotReloader()
        {
        }

        public bool IsRunning { get; private set; }

        public void InitComponent(Element element, Action defaultInitializer = null)
        {
            if (!IsRunning)
            {
                defaultInitializer?.Invoke();
                OnLoaded(element);
                return;
            }

            element.PropertyChanged += OnElementPropertyChanged;

            var elementType = element.GetType();
            var className = RetriveClassName(elementType);
            if (!_fileMapping.TryGetValue(className, out ReloadItem item))
            {
                item = new ReloadItem();
            }
            item.Elements.Add(new ElementEntry
            {
                Element = element
            });

            if (string.IsNullOrWhiteSpace(item.Xaml))
            {
                _fileMapping[className] = item;

                if (defaultInitializer != null)
                {
                    defaultInitializer.Invoke();
                    OnLoaded(element);
                    return;
                }

                try
                {
                    element.LoadFromXaml(elementType);
                }
                catch (XamlParseException)
                {
                    elementType.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                        .FirstOrDefault(m => m.IsDefined(typeof(GeneratedCodeAttribute), true))
                        .Invoke(element, null);
                }
                SetupNamedChildren(element);
                OnLoaded(element);
                return;
            }

            ReloadElement(element, item.Xaml);
        }

        public void Stop()
        {
            IsRunning = false;
            _reloaderCancellationTokenSource?.Cancel();
            _subscription?.Dispose();
            _subscription = null;
            _fileMapping = null;
        }

        public void Start(int port = 8000)
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                          .SelectMany(x => x.GetIPProperties().UnicastAddresses)
                          .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork)
                          .Select(x => x.Address.MapToIPv4())
                          .Where(x => x.ToString() != "127.0.0.1")
                          .ToArray();

            foreach (var address in addresses)
            {
                Console.WriteLine($"HOTRELOAD AVAILABLE IP: {address}");
            }

            var ip = addresses.FirstOrDefault()?.ToString() ?? "127.0.0.1";
            Start(ip, port);
        }

        public void Start(string ip, int port) => Start($"http://{ip}:{port}");

        public void Start(string url)
        {
            Stop();
            IsRunning = true;

            _fileMapping = new ConcurrentDictionary<string, ReloadItem>();
            _reloaderCancellationTokenSource = new CancellationTokenSource();

            var uri = new Uri(url);
            var tcpListener = new TcpListener(uri.Host.GetIPv4Address(), uri.Port)
            {
                ExclusiveAddressUse = false
            };

            var httpSender = new HttpSender();
            _subscription = tcpListener.ToHttpListenerObservable(_reloaderCancellationTokenSource.Token)
                        .Select(r => Observable.FromAsync(() => HandleRequestAsync(r, httpSender)))
                        .Concat()
                        .Subscribe();

            Console.WriteLine($"HOTRELOAD STARTED AT {url}");
        }

        private async Task HandleRequestAsync(IHttpRequestResponse request, HttpSender httpSender)
        {
            if (request.RequestType == RequestType.TCP &&
                    request.Method == HttpMethod.Post.Method &&
                    request.Path.StartsWith("/reload", StringComparison.InvariantCulture))
            {
                var xaml = Encoding.UTF8.GetString(request.Body.ToArray());
                var className = RetriveClassName(xaml);

                if (string.IsNullOrWhiteSpace(className))
                {
                    Debug.WriteLine("HOTRELOAD ERROR: 'x:Class' NOT FOUND.");
                    return;
                }

                if (!_fileMapping.TryGetValue(className, out ReloadItem item))
                {
                    item = new ReloadItem();
                    _fileMapping[className] = item;
                }
                var oldXaml = item.Xaml;
                item.Xaml = xaml;

                ReloadElements(className, item, oldXaml);
            }

            var response = new HttpResponse
            {
                StatusCode = (int)HttpStatusCode.OK
            };
            await httpSender.SendTcpResponseAsync(request, response).ConfigureAwait(false);
        }

        private string RetriveClassName(string xaml)
        => Regex.Match(xaml, "x:Class=\"(.+)\"").Groups[1].Value;

        private string RetriveClassName(Type type)
        => type.FullName;

        private void ReloadElements(string classFullName, ReloadItem item, string oldXaml)
        {
            if (!item.Elements.Any())
            {
                return;
            }

            var nameParts = classFullName.Split('.');
            var nameSpace = string.Join(".", nameParts.Take(nameParts.Length - 1));
            var className = nameParts[nameParts.Length - 1];

            var updatedElements = item.Elements.Where(e => e.HasRenderer).Select(e => e.Element).ToArray();

            var affectedItems = _fileMapping.Values.Where(e => Regex.IsMatch(e.Xaml, $"\"clr-namespace:{nameSpace}")
                                              && Regex.IsMatch(e.Xaml, $":{className}")).ToArray();

            Device.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    foreach (var element in updatedElements)
                    {
                        ReloadElement(element, item.Xaml);
                    }

                    foreach (var affectedItem in affectedItems)
                    {
                        foreach (var element in affectedItem.Elements.Where(e => e.HasRenderer).Select(e => e.Element).ToArray())
                        {
                            ReloadElement(element, affectedItem.Xaml);
                        }
                    }
                }
                catch
                {
                    Debug.WriteLine("HOTRELOAD ERROR: CANNOT PARSE XAML.");
                    item.Xaml = oldXaml;
                }
            });
        }

        private void ReloadElement(Element element, string xaml)
        {
            switch (element)
            {
                case ContentPage contentPage:
                    contentPage.Content = null;
                    break;
                case ContentView contentView:
                    contentView.Content = null;
                    break;
                case ScrollView scrollView:
                    scrollView.Content = null;
                    break;
                case ViewCell viewCell:
                    viewCell.View = null;
                    break;
                case Layout<View> layout:
                    layout.Children.Clear();
                    break;
            }
            element.LoadFromXaml(xaml);
            SetupNamedChildren(element);
            OnLoaded(element);
        }

        private void SetupNamedChildren(Element element)
        {
            var fields = element.GetType()
                .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(f => f.IsDefined(typeof(GeneratedCodeAttribute), true));

            foreach (var field in fields)
            {
                var value = element.FindByName<object>(field.Name);
                field.SetValue(element, value);
            }
        }

        private void OnLoaded(Element element) => (element as IReloadable)?.OnLoaded();

        private void OnElementPropertyChanged(object sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != "Renderer")
            {
                return;
            }

            var element = sender as Element;
            var className = RetriveClassName(element.GetType());
            if (!_fileMapping.TryGetValue(className, out ReloadItem item))
            {
                return;
            }

            var entry = item.Elements.FirstOrDefault(x => x.Element == element);
            if (entry == null)
            {
                return;
            }
            if (entry.HasRenderer)
            {
                entry.Element.PropertyChanged -= OnElementPropertyChanged;
                entry.HasRenderer = false;
                item.Elements.Remove(entry);
                return;
            }

            entry.HasRenderer = true;
        }
    }
}