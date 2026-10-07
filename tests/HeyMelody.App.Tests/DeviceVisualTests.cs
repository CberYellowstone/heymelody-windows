using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HeyMelodyWindows.App;

internal static class DeviceVisualTests
{
    public static int Run(HeadlessUnitTestSession session)
    {
        Run("unconnected X4 aliases use embedded artwork without enabling settings", NameFallbackAsync);
        Run("actual product IDs override names and stay with their device across session changes", ProductIdentityAsync);
        Run("removed devices and replacement data contexts discard temporary product knowledge", CacheLifetimeAsync);
        Run("visual hosts support independent interactive templates and share decoded images", TemplateAndResourceAsync);
        Run("visual updates preserve device containers and manual scrolling without extra device work", VisualUpdateLayoutAsync);
        return 5;

        void Run(string name, Func<Task> test)
        {
            session.Dispatch<bool>(async () => { await test(); return true; }, CancellationToken.None)
                .GetAwaiter().GetResult();
            Console.WriteLine("PASS " + name);
        }
    }

    public static void CheckScenario(MainView view, string scenario)
    {
        var x4 = scenario is "connected" or "partial" or "disconnected";
        CheckHero(view, x4);
        var picker = view.FindControl<ListBox>("DevicePicker")!;
        if (scenario != "empty") CheckIcon(picker, 0, x4);
    }

    private static async Task NameFallbackAsync()
    {
        var names = new[] { "OPPO Enco X4", "Enco X4", " oppo enco x4 ", "\tENCO X4\r\n",
            "OPPO Enco X4 Pro", "LE OPPO Enco X4", "OPPO Enco X40", "普通耳机" };
        var service = new VisualService(names);
        await using var vm = new MainViewModel(service, new NoConfirm());
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = CreateWindow(view);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var picker = view.FindControl<ListBox>("DevicePicker")!;
            for (var index = 0; index < names.Length; index++)
            {
                CheckIcon(picker, index, index < 4);
                await vm.SelectDeviceAsync(service.Devices[index]);
                Dispatcher.UIThread.RunJobs();
                CheckHero(view, index < 4);
                Check(!vm.HasVerifiedSnapshot && !view.FindControl<ComboBox>("AncCombo")!.IsEnabled,
                    "A visual name match grants no verified session or headset controls.");
            }
            await vm.SelectDeviceAsync(null); Dispatcher.UIThread.RunJobs();
            CheckHero(view, false);
            Check(service.Enumerations == 1 && service.Connections == 0 && service.Disconnections == 0
                && service.Reads == 0 && service.Submissions == 0,
                "Name-based artwork resolution makes no device requests beyond initial enumeration.");
        }
        finally { window.Close(); }
    }

    private static async Task SwitchingCannotBorrowProductAsync()
    {
        foreach (var sourceIsX4 in new[] { true, false })
        {
            var service = new VisualService(["已识别的改名耳机", sourceIsX4 ? "普通目标耳机" : "Enco X4"]);
            service.Products[service.Devices[0].Identity] = sourceIsX4 ? "06EC10" : "未知";
            await using var vm = new MainViewModel(service, new NoConfirm());
            await vm.InitializeAsync();
            var view = new MainView { DataContext = vm };
            var window = CreateWindow(view);
            try
            {
                window.Show(); Dispatcher.UIThread.RunJobs();
                await vm.ConnectCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
                var picker = view.FindControl<ListBox>("DevicePicker")!;
                CheckHero(view, sourceIsX4); CheckIcon(picker, 0, sourceIsX4);
                await vm.SelectDeviceAsync(service.Devices[1]); Dispatcher.UIThread.RunJobs();
                Check(vm.SelectedDevice?.Identity == service.Devices[1].Identity && vm.ProductId is null
                    && !vm.HasVerifiedSnapshot, "Switching leaves the new device unconnected with no protocol snapshot.");
                CheckHero(view, !sourceIsX4); CheckIcon(picker, 1, !sourceIsX4);
                CheckIcon(picker, 0, sourceIsX4);
                Check(service.Enumerations == 1 && service.Connections == 1 && service.Disconnections == 1
                    && service.Reads == 0 && service.Submissions == 0,
                    "A direct device switch retains each device's own appearance without extra protocol work.");
            }
            finally { window.Close(); }
        }
    }

    private static async Task ProductIdentityAsync()
    {
        var service = new VisualService(["OPPO Enco X4", "我的改名耳机", "Enco X4"]);
        service.Products[service.Devices[0].Identity] = "未知";
        service.Products[service.Devices[1].Identity] = "06EC10";
        service.Products[service.Devices[2].Identity] = "06EC11";
        await using var vm = new MainViewModel(service, new NoConfirm());
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = CreateWindow(view);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var picker = view.FindControl<ListBox>("DevicePicker")!;
            CheckHero(view, true);
            await vm.ConnectCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
            CheckHero(view, false); CheckIcon(picker, 0, false);
            Check(!vm.HasVerifiedSnapshot && !view.FindControl<ComboBox>("AncCombo")!.IsEnabled,
                "An actual unknown product overrides an X4 name without enabling unadapted controls.");
            await vm.SelectDeviceAsync(service.Devices[1]); Dispatcher.UIThread.RunJobs();
            CheckHero(view, false); CheckIcon(picker, 1, false);
            await vm.ConnectCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
            CheckHero(view, true); CheckIcon(picker, 1, true);
            Check(vm.HasVerifiedSnapshot && view.FindControl<ComboBox>("AncCombo")!.IsEnabled,
                "A renamed real X4 uses its actual product ID and keeps normal verified controls.");
            await vm.ConnectCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
            CheckHero(view, true); CheckIcon(picker, 1, true);
            Check(vm.ProductId is null && !vm.HasVerifiedSnapshot,
                "Artwork knowledge survives disconnect independently of a live protocol snapshot.");
            await vm.SelectDeviceAsync(service.Devices[2]); Dispatcher.UIThread.RunJobs();
            CheckHero(view, true);
            await vm.ConnectCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
            CheckHero(view, false); CheckIcon(picker, 2, false);
            await vm.SelectDeviceAsync(service.Devices[0]); Dispatcher.UIThread.RunJobs();
            CheckHero(view, false); CheckIcon(picker, 0, false); CheckIcon(picker, 1, true);
            await vm.ConnectCommand.ExecuteAsync(null);
            service.ReadOverride = new UiSnapshot
            {
                Identity = service.Devices[1].Identity, DeviceName = "错误身份回读", ProductId = "06EC10",
                Connected = true, SupportedModel = true, ConnectionState = ConnectionState.Connected
            };
            await vm.RefreshStateCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
            CheckHero(view, false); CheckIcon(picker, 0, false); CheckIcon(picker, 1, true);
            Check(vm.ProductId is null && !vm.HasVerifiedSnapshot && service.Reads == 1,
                "A rejected snapshot from another identity cannot alter the selected device's artwork.");
            Check(service.Enumerations == 1 && service.Connections == 4 && service.Disconnections == 3
                && service.Submissions == 0,
                "Artwork updates add no enumeration, connection, disconnect or settings operation.");
        }
        finally { window.Close(); }
        await SwitchingCannotBorrowProductAsync();
    }

    private static async Task CacheLifetimeAsync()
    {
        var service = new VisualService(["OPPO Enco X4", "另一副耳机"]);
        var original = service.Devices[0];
        service.Products[original.Identity] = "其他型号";
        await using var vm = new MainViewModel(service, new NoConfirm());
        await vm.InitializeAsync();
        var replacementService = new VisualService(["OPPO Enco X4"]);
        await using var replacementVm = new MainViewModel(replacementService, new NoConfirm());
        await replacementVm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = CreateWindow(view);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await vm.ConnectCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
            CheckHero(view, false);
            service.Devices = [service.Devices[1]];
            await vm.RefreshDevicesCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
            CheckHero(view, false);
            service.Devices = [original, service.Devices[0]];
            await vm.RefreshDevicesCommand.ExecuteAsync(null);
            await vm.SelectDeviceAsync(original); Dispatcher.UIThread.RunJobs();
            CheckHero(view, true); CheckIcon(view.FindControl<ListBox>("DevicePicker")!, 0, true);
            await vm.ConnectCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
            CheckHero(view, false);
            var oldCatalog = view.VisualCatalog;
            view.DataContext = replacementVm; Dispatcher.UIThread.RunJobs();
            Check(ReferenceEquals(oldCatalog, view.VisualCatalog), "The host keeps one catalog while replacing its data context.");
            CheckHero(view, true); CheckIcon(view.FindControl<ListBox>("DevicePicker")!, 0, true);
            Check(replacementService.Devices[0].Identity == original.Identity && replacementService.Connections == 0,
                "A replacement view model with the same identity starts with name fallback, not old product knowledge.");
            view.DataContext = null; Dispatcher.UIThread.RunJobs();
            CheckHero(view, false);
            Check(service.Enumerations == 3 && service.Connections == 2 && service.Disconnections == 1
                && service.Submissions == 0 && replacementService.Enumerations == 1
                && replacementService.Submissions == 0,
                "Catalog pruning and context replacement add no headset work.");
        }
        finally { window.Close(); }
    }

    private static Task TemplateAndResourceAsync()
    {
        var hero = new ProductArtwork { Visual = DeviceVisuals.X4Product, Width = 240, Height = 180 };
        var anotherHero = new ProductArtwork { Visual = new BitmapVisual(DeviceVisuals.X4Product.Source), Width = 240, Height = 180 };
        var icon = new DeviceIconView { Visual = DeviceVisuals.X4Icon, Width = 28, Height = 28 };
        var window = CreateWindow(new StackPanel { Spacing = 8, Children = { hero, anotherHero, icon } });
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var firstImage = ImageFor(hero); var secondImage = ImageFor(anotherHero);
            Check(!ReferenceEquals(firstImage, secondImage) && ReferenceEquals(firstImage.Source, secondImage.Source),
                "The same resource URI reuses one decoded Bitmap while each host creates its own Image.");
            Check(firstImage.Stretch == Stretch.Uniform && ImageFor(icon).Stretch == Stretch.Uniform
                && icon.Bounds.Size == new Size(28, 28), "PNG templates preserve aspect ratio in both host sizes.");
            var source = firstImage.Source;
            hero.Visual = DeviceVisuals.GenericProduct; Dispatcher.UIThread.RunJobs();
            Check(!hero.GetVisualDescendants().OfType<Image>().Any(), "Switching to a generic visual replaces the image presentation.");
            hero.Visual = DeviceVisuals.X4Product; Dispatcher.UIThread.RunJobs();
            Check(ReferenceEquals(source, ImageFor(hero).Source), "Switching back to X4 reuses its existing decoded image.");
            var clicks = 0;
            var content = new object();
            var template = new FuncDataTemplate<object>((value, _) =>
            {
                Check(ReferenceEquals(value, content), "Custom templates receive the visual payload unchanged.");
                var button = new Button { Content = "V", MinWidth = 0, MinHeight = 0, Padding = default };
                button.Click += (_, _) => clicks++;
                return button;
            });
            hero.Visual = content; icon.Visual = content;
            hero.VisualTemplate = template; icon.VisualTemplate = template; Dispatcher.UIThread.RunJobs();
            var heroButton = hero.GetVisualDescendants().OfType<Button>().Single();
            var iconButton = icon.GetVisualDescendants().OfType<Button>().Single();
            Check(!ReferenceEquals(heroButton, iconButton) && heroButton.IsEffectivelyEnabled && heroButton.IsHitTestVisible
                && heroButton.GetVisualAncestors().All(visual => visual is not Viewbox),
                "Custom presentations are independent controls with input enabled and no imposed scaling container.");
            Click(window, heroButton); Click(window, iconButton);
            Check(clicks == 2, "Both large and small visual hosts deliver pointer input to custom interactive content.");
            hero.VisualTemplate = DeviceVisuals.DefaultTemplate; icon.VisualTemplate = DeviceVisuals.DefaultTemplate;
            hero.Visual = DeviceVisuals.X4Product; icon.Visual = DeviceVisuals.X4Icon;
            Dispatcher.UIThread.RunJobs();
            Check(ReferenceEquals(source, ImageFor(hero).Source) && ImageFor(icon).Source is not null,
                "The PNG presentation can be restored after using a custom template.");
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    }

    private static async Task VisualUpdateLayoutAsync()
    {
        var service = new VisualService(Enumerable.Range(0, 8).Select(index => "改名耳机 " + index).ToArray());
        service.Products[service.Devices[0].Identity] = "06EC10";
        await using var vm = new MainViewModel(service, new NoConfirm());
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = CreateWindow(view);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var picker = view.FindControl<ListBox>("DevicePicker")!;
            var first = Card(picker, 0);
            var scroll = view.FindControl<ScrollViewer>("DeviceBarScroll")!;
            scroll.Offset = new Vector((scroll.Extent.Width - scroll.Viewport.Width) / 3, 0);
            Dispatcher.UIThread.RunJobs();
            var offset = scroll.Offset.X;
            var cardSize = first.Bounds.Size;
            CheckHero(view, false); CheckIcon(picker, 0, false);
            await vm.ConnectCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
            CheckHero(view, true); CheckIcon(picker, 0, true);
            await Task.Delay(30); Dispatcher.UIThread.RunJobs();
            Check(ReferenceEquals(first, Card(picker, 0)) && first.Bounds.Size == cardSize
                && Math.Abs(scroll.Offset.X - offset) <= 1,
                "Updating the large image and card icon preserves the list containers, card dimensions and manual scroll position.");
            Check(DeviceVisuals.X4Product.Source.Scheme == "avares" && DeviceVisuals.X4Icon.Source.Scheme == "avares"
                && DeviceVisuals.X4Product.Source.AbsolutePath == "/Assets/Devices/EncoX4/x4-pods-with-earphones.png"
                && DeviceVisuals.X4Icon.Source.AbsolutePath == "/Assets/Devices/EncoX4/x4-both-earphones.png",
                "Both PNG descriptors resolve embedded project assets rather than the research directory.");
            DeviceBarTests.CheckBottomBar(view, window);
            Check(service.Enumerations == 1 && service.Connections == 1 && service.Disconnections == 0
                && service.Reads == 0 && service.Submissions == 0,
                "One explicit connection is the only device work associated with resolving the new artwork.");
        }
        finally { window.Close(); }
    }

    private static void CheckHero(MainView view, bool x4)
    {
        var hero = view.FindControl<ProductArtwork>("HeroArt")!;
        Check(Equals(hero.Visual, x4 ? DeviceVisuals.X4Product : DeviceVisuals.GenericProduct),
            "Selected device artwork follows its own product knowledge and exact name fallback.");
        if (x4) Check(ImageFor(hero).Source is not null, "X4 product artwork decodes its embedded PNG.");
        else Check(!hero.GetVisualDescendants().OfType<Image>().Any(), "Generic product artwork contains no X4 PNG.");
    }

    private static void CheckIcon(ListBox picker, int index, bool x4)
    {
        var icon = Card(picker, index).GetVisualDescendants().OfType<DeviceIconView>().Single();
        Check(Equals(icon.Visual, x4 ? DeviceVisuals.X4Icon : DeviceVisuals.GenericIcon)
            && icon.Bounds.Size == new Size(28, 28), "Each card independently resolves its visual in the fixed icon slot.");
        if (x4) Check(ImageFor(icon).Source is not null, "X4 device icons decode their embedded PNG.");
        else Check(!icon.GetVisualDescendants().OfType<Image>().Any(), "Generic card icons contain no X4 PNG.");
    }

    private static Image ImageFor(Control host) => host.GetVisualDescendants().OfType<Image>().Single();
    private static ListBoxItem Card(ListBox picker, int index) => picker.ContainerFromIndex(index) as ListBoxItem
        ?? throw new InvalidOperationException("Device visual card was not realized.");
    private static Window CreateWindow(Control view) => new()
        { Width = 1280, Height = 900, Content = view, WindowDecorations = WindowDecorations.None };
    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class VisualService(string[] names) : IHeadsetService
    {
        private readonly PreviewService _inner = new();
        private string? _identity;
        public UiDevice[] Devices { get; set; } = names.Select((name, index) => new UiDevice("visual-device-" + index, name, false)).ToArray();
        public Dictionary<string, string?> Products { get; } = [];
        public UiSnapshot? ReadOverride { get; set; }
        public int Enumerations { get; private set; }
        public int Connections { get; private set; }
        public int Disconnections { get; private set; }
        public int Reads { get; private set; }
        public int Submissions { get; private set; }
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token)
        { Enumerations++; return Task.FromResult<IReadOnlyList<UiDevice>>(Devices); }
        public async Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token)
        {
            Connections++; _identity = identity;
            return Adjust(await _inner.ConnectAsync(identity, token));
        }
        public Task DisconnectAsync(CancellationToken token)
        { Disconnections++; _identity = null; return Task.CompletedTask; }
        public async Task<UiSnapshot> RefreshAsync(CancellationToken token)
        {
            Reads++;
            return ReadOverride ?? Adjust(await _inner.RefreshAsync(token));
        }
        private UiSnapshot Adjust(UiSnapshot snapshot)
        {
            snapshot.Identity = _identity!;
            snapshot.DeviceName = Devices.Single(device => device.Identity == _identity).Name;
            snapshot.ProductId = Products.GetValueOrDefault(_identity!);
            snapshot.SupportedModel = snapshot.ProductId == "06EC10";
            return snapshot;
        }
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        { Submissions++; throw new InvalidOperationException("Artwork tests must not submit headset settings."); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
