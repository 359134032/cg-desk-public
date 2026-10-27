using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using WinRT;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace CgDesk.Services;

internal static class FsrSurface
{
    const int Child = 0x40000000;
    const int ClipSiblings = 0x04000000;
    const int NoActivateEx = 0x08000000;
    const uint NoActivate = 0x0010;
    const uint ShowFlag = 0x0040;
    const uint HideFlag = 0x0080;
    const int SetCursorMessage = 0x0020;
    const int MouseActivate = 0x0021;
    const int EraseBackground = 0x0014;

    static readonly Guid ItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    static readonly Guid TextureGuid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    static readonly object Gate = new();

    static IntPtr _host;
    static IntPtr _game;
    static IntPtr _hwnd;
    static WndProc? _proc;
    static bool _classReady;
    static volatile bool _presenting;
    static volatile bool _failed;
    static string _lastError = "";
    static string _captureStep = "";
    static float _sharpness = 0.7f;
    static long _captureStarted;
    static long _renderedFrames;
    static int _viewW;
    static int _viewH;
    static int _captureW;
    static int _captureH;

    static ID3D11Device? _device;
    static ID3D11DeviceContext? _context;
    static IDXGISwapChain? _swap;
    static ID3D11RenderTargetView? _backRtv;
    static ID3D11Texture2D? _easuTex;
    static ID3D11RenderTargetView? _easuRtv;
    static ID3D11ShaderResourceView? _easuSrv;
    static ID3D11Buffer? _easuCb;
    static ID3D11Buffer? _rcasCb;
    static ID3D11VertexShader? _vs;
    static ID3D11PixelShader? _easuPs;
    static ID3D11PixelShader? _rcasPs;
    static ID3D11SamplerState? _sampler;
    static IDirect3DDevice? _winrtDevice;
    static GraphicsCaptureItem? _item;
    static Direct3D11CaptureFramePool? _pool;
    static GraphicsCaptureSession? _session;

    public static bool IsPresenting => _presenting;
    public static bool Failed => _failed;
    public static string LastError => _lastError;
    public static long RenderedFrames => Interlocked.Read(ref _renderedFrames);
    public static (int Width, int Height) SourceSize => (_captureW, _captureH);
    public static (int Width, int Height) OutputSize => (_viewW, _viewH);

    public static void SetSharpness(float value) => _sharpness = Math.Clamp(value, 0f, 1f);

    public static void Reset()
    {
        lock (Gate)
        {
            _failed = false;
            _lastError = "";
            _presenting = false;
            _captureStarted = 0;
            Interlocked.Exchange(ref _renderedFrames, 0);
            StopCapture();
            HideWindow();
        }
    }

    public static void Hide()
    {
        lock (Gate)
        {
            StopCapture();
            HideWindow();
            _presenting = false;
            _captureStarted = 0;
        }
    }

    public static void Close()
    {
        lock (Gate)
        {
            StopCapture();
            DestroyWindow();
            DisposeDevice();
            _presenting = false;
            _failed = false;
            _lastError = "";
        }
    }

    public static void Sync(IntPtr host, IntPtr game, float sharpness)
    {
        lock (Gate)
        {
            _sharpness = Math.Clamp(sharpness, 0f, 1f);
            if (_failed)
            {
                StopCapture();
                HideWindow();
                return;
            }

            if (host == IntPtr.Zero || game == IntPtr.Zero || !IsWindow(host) || !IsWindow(game))
            {
                StopCapture();
                HideWindow();
                _presenting = false;
                return;
            }

            var stage = "创建绘制窗口";
            try
            {
                EnsureWindow(host);
                stage = "初始化 GPU 和着色器";
                EnsureDevice();
                stage = "创建窗口采集";
                EnsureCapture(game);
                stage = "创建输出画面";
                var size = ClientSize(host);
                if (size.Width >= 8 && size.Height >= 8 && (size.Width != _viewW || size.Height != _viewH || _backRtv == null))
                {
                    Resize(size.Width, size.Height);
                }

                PlaceWindow();
                if (!_presenting && _captureStarted != 0 && Environment.TickCount64 - _captureStarted > 2000)
                {
                    Fail("没有采集到画面，主窗口保持原来的大小。");
                }
            }
            catch (Exception ex)
            {
                var detail = stage == "创建窗口采集" ? $"：{_captureStep}" : "";
                Fail($"画面放大没能启动（{stage}{detail}，0x{ex.HResult:X8}）。" + Brief(ex));
            }
        }
    }

    static void OnFrame(Direct3D11CaptureFramePool sender, object args)
    {
        lock (Gate)
        {
            if (_failed || !ReferenceEquals(sender, _pool) || _device == null || _context == null || _backRtv == null)
            {
                return;
            }

            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame == null)
                {
                    return;
                }

                var content = frame.ContentSize;
                if (content.Width > 2 && content.Height > 2 && (content.Width != _captureW || content.Height != _captureH))
                {
                    _captureW = content.Width;
                    _captureH = content.Height;
                    _pool?.Recreate(
                        _winrtDevice,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized,
                        2,
                        content);
                }

                if (!Draw(frame)) return;
                Interlocked.Increment(ref _renderedFrames);
                if (!_presenting)
                {
                    _presenting = true;
                    PlaceWindow();
                }
            }
            catch (Exception ex)
            {
                _lastError = "画面放大中断。" + Brief(ex);
                _failed = true;
                _presenting = false;
                HideWindow();
            }
        }
    }

    static bool Draw(Direct3D11CaptureFrame frame)
    {
        if (_device == null || _context == null || _backRtv == null || _easuRtv == null || _easuSrv == null || _easuCb == null || _rcasCb == null)
        {
            return false;
        }

        using var texture = OpenTexture(frame.Surface);
        if (texture == null)
        {
            throw new InvalidOperationException("无法读取采集画面的 Direct3D 纹理。");
        }

        var desc = texture.Description;
        var inputW = (int)desc.Width;
        var inputH = (int)desc.Height;
        if (inputW < 2 || inputH < 2 || _viewW < 8 || _viewH < 8)
        {
            return false;
        }

        using var source = _device.CreateShaderResourceView(texture, null);
        var easu = new uint[16];
        WriteEasu(easu, inputW, inputH, _viewW, _viewH);
        Upload(_easuCb, easu);
        var rcas = new uint[8];
        WriteRcas(rcas, _sharpness, (uint)_viewW, (uint)_viewH);
        Upload(_rcasCb, rcas);

        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ID3D11ClassInstance[] classes = [];
        _context.VSSetShader(_vs, classes, 0);
        _context.PSSetSampler(0, _sampler);
        _context.RSSetViewport(0, 0, _viewW, _viewH, 0, 1);

        _context.OMSetRenderTargets(_easuRtv, null);
        _context.PSSetShaderResource(0, source);
        _context.PSSetConstantBuffer(0, _easuCb);
        _context.PSSetShader(_easuPs, classes, 0);
        _context.Draw(3, 0);

        _context.PSSetShaderResource(0, null!);
        _context.OMSetRenderTargets(_backRtv, null);
        _context.PSSetShaderResource(0, _easuSrv);
        _context.PSSetConstantBuffer(0, _rcasCb);
        _context.PSSetShader(_rcasPs, classes, 0);
        _context.Draw(3, 0);
        _context.PSSetShaderResource(0, null!);
        if (_swap == null) return false;
        _swap.Present(0, PresentFlags.None).CheckError();
        return true;
    }

    static unsafe void Upload(ID3D11Buffer buffer, uint[] words)
    {
        var mapped = _context!.Map(buffer, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        unsafe
        {
            var dest = (uint*)mapped.DataPointer;
            for (var i = 0; i < words.Length; i++)
            {
                dest[i] = words[i];
            }
        }

        _context.Unmap(buffer, 0);
    }

    static void WriteEasu(uint[] dest, int inputW, int inputH, int outputW, int outputH)
    {
        var inX = (float)inputW;
        var inY = (float)inputH;
        var outX = (float)outputW;
        var outY = (float)outputH;
        var invW = 1f / inX;
        var invH = 1f / inY;
        dest[0] = Bits(inX / outX);
        dest[1] = Bits(inY / outY);
        dest[2] = Bits(0.5f * inX / outX - 0.5f);
        dest[3] = Bits(0.5f * inY / outY - 0.5f);
        dest[4] = Bits(invW);
        dest[5] = Bits(invH);
        dest[6] = Bits(invW);
        dest[7] = Bits(-invH);
        dest[8] = Bits(-invW);
        dest[9] = Bits(2f * invH);
        dest[10] = Bits(invW);
        dest[11] = Bits(2f * invH);
        dest[12] = Bits(0f);
        dest[13] = Bits(4f * invH);
        dest[14] = 0;
        dest[15] = 0;
    }

    static void WriteRcas(uint[] dest, float clarity, uint width, uint height)
    {
        var stops = (1f - Math.Clamp(clarity, 0f, 1f)) * 2f;
        dest[0] = Bits(MathF.Pow(2f, -stops));
        dest[1] = 0;
        dest[2] = 0;
        dest[3] = 0;
        dest[4] = width;
        dest[5] = height;
        dest[6] = 0;
        dest[7] = 0;
    }

    static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    static void EnsureCapture(IntPtr game)
    {
        if (_session != null && _game == game && _pool != null)
        {
            return;
        }

        StopCapture();
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new InvalidOperationException("这台系统不能采集窗口画面。");
        }

        _game = game;
        _captureStep = "CreateForWindow";
        _item = CreateItem(game);
        var size = _item.Size;
        if (size.Width < 2 || size.Height < 2)
        {
            var client = ClientSize(game);
            size = new SizeInt32(Math.Max(client.Width, 2), Math.Max(client.Height, 2));
        }

        _captureW = size.Width;
        _captureH = size.Height;
        _captureStep = "CreateFreeThreaded";
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            size);
        _pool.FrameArrived += OnFrame;
        _captureStep = "CreateCaptureSession";
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = false;
        TryHideBorder(_session);
        _captureStep = "StartCapture";
        _session.StartCapture();
        _captureStarted = Environment.TickCount64;
    }

    static void EnsureDevice()
    {
        if (_device != null)
        {
            return;
        }

        FsrShaders.Compile();
        D3D11.D3D11CreateDevice(
            IntPtr.Zero,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0],
            out _device,
            out _,
            out _context).CheckError();
        _vs = CreateVertex(_device, FsrShaders.VertexBytecode);
        _easuPs = CreatePixel(_device, FsrShaders.EasuBytecode);
        _rcasPs = CreatePixel(_device, FsrShaders.RcasBytecode);
        _easuCb = _device.CreateBuffer(64, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);
        _rcasCb = _device.CreateBuffer(32, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);
        _sampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue
        });
        using var dxgi = _device.QueryInterface<IDXGIDevice>();
        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var punk);
        if (hr < 0 || punk == IntPtr.Zero)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        try { _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(punk); }
        finally { Marshal.Release(punk); }
    }

    static unsafe ID3D11VertexShader CreateVertex(ID3D11Device device, byte[] bytecode)
    {
        fixed (byte* ptr = bytecode)
        {
            return device.CreateVertexShader(ptr, (nuint)bytecode.Length, null!);
        }
    }

    static unsafe ID3D11PixelShader CreatePixel(ID3D11Device device, byte[] bytecode)
    {
        fixed (byte* ptr = bytecode)
        {
            return device.CreatePixelShader(ptr, (nuint)bytecode.Length, null!);
        }
    }

    static void Resize(int width, int height)
    {
        if (_device == null || _hwnd == IntPtr.Zero)
        {
            return;
        }

        _viewW = width;
        _viewH = height;
        _backRtv?.Dispose();
        _backRtv = null;
        _easuSrv?.Dispose();
        _easuRtv?.Dispose();
        _easuTex?.Dispose();
        _easuSrv = null;
        _easuRtv = null;
        _easuTex = null;
        if (_swap == null)
        {
            using var dxgi = _device.QueryInterface<IDXGIDevice>();
            dxgi.GetAdapter(out var adapter).CheckError();
            using (adapter)
            {
                using var factory = adapter.GetParent<IDXGIFactory>();
                var desc = new SwapChainDescription
                {
                    BufferCount = 1,
                    BufferDescription = new ModeDescription((uint)width, (uint)height, new Rational(0, 1), Format.B8G8R8A8_UNorm),
                    BufferUsage = Usage.RenderTargetOutput,
                    OutputWindow = _hwnd,
                    SampleDescription = new SampleDescription(1, 0),
                    Windowed = true,
                    SwapEffect = SwapEffect.Discard
                };
                _swap = factory.CreateSwapChain(_device, desc);
                factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll);
            }
        }
        else
        {
            _swap.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None);
        }

        using var back = _swap.GetBuffer<ID3D11Texture2D>(0);
        _backRtv = _device.CreateRenderTargetView(back, null);
        _easuTex = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
        });
        _easuRtv = _device.CreateRenderTargetView(_easuTex, null);
        _easuSrv = _device.CreateShaderResourceView(_easuTex, null);
    }

    static unsafe ID3D11Texture2D? OpenTexture(IDirect3DSurface? surface)
    {
        if (surface == null)
        {
            return null;
        }

        using var surfaceReference = MarshalInterface<IDirect3DSurface>.CreateMarshaler(surface);
        var unknown = surfaceReference.ThisPtr;
        var accessGuid = typeof(IDirect3DDxgiInterfaceAccess).GUID;
        var hr = Marshal.QueryInterface(unknown, in accessGuid, out var accessPtr);
        if (hr < 0 || accessPtr == IntPtr.Zero)
        {
            return null;
        }

        var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPtr);
        Marshal.Release(accessPtr);
        try
        {
            var textureGuid = TextureGuid;
            hr = access.GetInterface(in textureGuid, out var texturePtr);
            if (hr < 0 || texturePtr == IntPtr.Zero)
            {
                return null;
            }

            return new ID3D11Texture2D(texturePtr);
        }
        finally
        {
            Marshal.ReleaseComObject(access);
        }
    }

    static GraphicsCaptureItem CreateItem(IntPtr hwnd)
    {
        var name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var hr = WindowsCreateString(name, name.Length, out var className);
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        try
        {
            var iid = typeof(IGraphicsCaptureItemInterop).GUID;
            hr = RoGetActivationFactory(className, ref iid, out var factoryPtr);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            var factory = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            Marshal.Release(factoryPtr);
            try
            {
                var itemIid = ItemGuid;
                hr = factory.CreateForWindow(hwnd, ref itemIid, out var itemPtr);
                if (hr < 0 || itemPtr == IntPtr.Zero)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                try { return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr); }
                finally { Marshal.Release(itemPtr); }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        finally
        {
            WindowsDeleteString(className);
        }
    }

    static void TryHideBorder(GraphicsCaptureSession session)
    {
        try
        {
            var unknown = Marshal.GetIUnknownForObject(session);
            var border = Marshal.GetObjectForIUnknown(unknown) as IGraphicsCaptureSession3;
            Marshal.Release(unknown);
            if (border != null)
            {
                border.IsBorderRequired = false;
            }
        }
        catch (Exception)
        {
        }
    }

    static void EnsureWindow(IntPtr host)
    {
        if (_hwnd != IntPtr.Zero && _host == host && IsWindow(_hwnd))
        {
            return;
        }

        DestroyWindow();
        _ = SetThreadDpiHostingBehavior(1);
        Register();
        _proc = OnMessage;
        _hwnd = CreateWindowExW(
            NoActivateEx,
            "CgDeskFsr",
            "",
            Child | ClipSiblings,
            0,
            0,
            0,
            0,
            host,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);
        _host = host;
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("放大画面窗口没有建起来。");
        }
    }

    static void PlaceWindow()
    {
        if (_hwnd == IntPtr.Zero || _viewW < 8 || _viewH < 8)
        {
            return;
        }

        var flags = NoActivate | (_presenting ? ShowFlag : HideFlag);
        _ = SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, _viewW, _viewH, flags);
    }

    static void HideWindow()
    {
        var wasVisible = _hwnd != IntPtr.Zero && NativeMethods.IsWindowVisible(_hwnd);
        var released = GamePointer.ReleaseOwnedCapture(_hwnd);
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
        {
            _ = ShowWindow(_hwnd, 0);
        }
        if (wasVisible || released) GamePointer.RestoreArrow();
    }

    static void Fail(string message)
    {
        _lastError = message;
        _failed = true;
        _presenting = false;
        StopCapture();
        HideWindow();
    }

    static void StopCapture()
    {
        if (_pool != null)
        {
            _pool.FrameArrived -= OnFrame;
        }

        _session?.Dispose();
        _pool?.Dispose();
        _session = null;
        _pool = null;
        _item = null;
        _game = IntPtr.Zero;
        _captureStarted = 0;
    }

    static void DestroyWindow()
    {
        HideWindow();
        ReleaseTargets();
        _swap?.Dispose();
        _swap = null;
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
        {
            _ = DestroyWindow(_hwnd);
        }

        _hwnd = IntPtr.Zero;
        _host = IntPtr.Zero;
        _viewW = 0;
        _viewH = 0;
    }

    static void ReleaseTargets()
    {
        _backRtv?.Dispose();
        _easuSrv?.Dispose();
        _easuRtv?.Dispose();
        _easuTex?.Dispose();
        _backRtv = null;
        _easuSrv = null;
        _easuRtv = null;
        _easuTex = null;
    }

    static void DisposeDevice()
    {
        ReleaseTargets();
        _swap?.Dispose();
        _swap = null;
        _sampler?.Dispose();
        _easuCb?.Dispose();
        _rcasCb?.Dispose();
        _easuPs?.Dispose();
        _rcasPs?.Dispose();
        _vs?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _winrtDevice?.Dispose();
        _sampler = null;
        _easuCb = null;
        _rcasCb = null;
        _easuPs = null;
        _rcasPs = null;
        _vs = null;
        _context = null;
        _device = null;
        _winrtDevice = null;
    }

    static void Register()
    {
        if (_classReady)
        {
            return;
        }

        _proc = OnMessage;
        var wndClass = new WndClass
        {
            Style = 8,
            WndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            Instance = GetModuleHandle(null),
            ClassName = "CgDeskFsr"
        };
        _classReady = RegisterClass(ref wndClass) != 0 || Marshal.GetLastWin32Error() == 1410;
    }

    static IntPtr OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == SetCursorMessage)
        {
            if (_presenting && _game != IntPtr.Zero && IsWindow(_game)) _ = SetCursor(IntPtr.Zero);
            else
            {
                GamePointer.ReleaseOwnedCapture(hwnd);
                GamePointer.RestoreArrow();
            }
            return new IntPtr(1);
        }

        if (msg == MouseActivate)
        {
            return new IntPtr(3);
        }

        if (msg == EraseBackground)
        {
            return new IntPtr(1);
        }

        if (msg == 0x001F)
        {
            GamePointer.ReleaseOwnedCapture(hwnd);
            GamePointer.RestoreArrow();
        }
        if (msg is 0x0202 or 0x0205 or 0x0208 or 0x020C && (wParam.ToInt32() & 0x73) == 0)
            GamePointer.ReleaseOwnedCapture(hwnd);
        if (msg is >= 0x0200 and <= 0x020E && (_game == IntPtr.Zero || !IsWindow(_game)))
        {
            GamePointer.ReleaseOwnedCapture(hwnd);
            GamePointer.RestoreArrow();
        }
        if (msg is >= 0x0200 and <= 0x020E && _game != IntPtr.Zero && IsWindow(_game))
        {
            if (msg is 0x0201 or 0x0204 or 0x0207 or 0x020B)
            {
                _ = SetCapture(hwnd);
            }

            var posted = lParam;
            if (msg is not (0x020A or 0x020E) && ClientSize(hwnd) is var view && ClientSize(_game) is var game
                && view.Width > 0 && view.Height > 0 && game.Width > 0 && game.Height > 0)
            {
                var packed = unchecked((int)(lParam.ToInt64() & 0xFFFFFFFF));
                var x = (short)(packed & 0xFFFF);
                var y = (short)((packed >> 16) & 0xFFFF);
                var nx = (int)Math.Clamp(x * (double)game.Width / view.Width, 0, game.Width - 1);
                var ny = (int)Math.Clamp(y * (double)game.Height / view.Height, 0, game.Height - 1);
                posted = new IntPtr((ny << 16) | (nx & 0xFFFF));
            }

            _ = PostMessage(_game, msg, wParam, posted);
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    static (int Width, int Height) ClientSize(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out var rect))
        {
            return (0, 0);
        }

        return (Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));
    }

    static string Brief(Exception ex)
    {
        var text = ex.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > 180 ? text[..180] : text;
    }

    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WndClass
    {
        public uint Style;
        public IntPtr WndProc;
        public int ClsExtra;
        public int WndExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? Menu;
        public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);

        [PreserveSig]
        int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig]
        int GetInterface(in Guid iid, out IntPtr result);
    }

    [ComImport]
    [Guid("f2cdd966-22ae-5ea1-9596-3a289344c3be")]
    [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    interface IGraphicsCaptureSession3
    {
        bool IsBorderRequired { get; set; }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", ExactSpelling = true)]
    static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ushort RegisterClass(ref WndClass wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr hwnd, out RectNative rect);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern IntPtr SetCapture(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    static extern int SetThreadDpiHostingBehavior(int value);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? name);
}
