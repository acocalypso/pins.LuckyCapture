using System.Reflection;

namespace PINS.LuckyCapture;

/// <summary>Reads through the already-open managed SDK under its owner's lock,
/// retaining the pull HRESULT before optional telemetry can overwrite it.</summary>
public sealed class ToupTekFrameReader {
    private readonly object sdk;
    private readonly Lock sdkLock;
    private readonly MethodInfo pull;
    private readonly PropertyInfo result;
    private readonly bool v4;

    public ToupTekFrameReader(object wrapper) {
        sdk = ReflectionAccess.Field<object>(wrapper, "sdk");
        sdkLock = ReflectionAccess.Field<Lock>(wrapper, "lockObj");
        result = sdk.GetType().GetProperty("HResult")
            ?? throw new LuckyCaptureException("SDK_LAYOUT_CHANGED", "ToupTek SDK HResult is unavailable");
        var methods = sdk.GetType().GetMethods().Where(m => m.ReturnType == typeof(bool)).ToArray();
        pull = methods.FirstOrDefault(m => Matches(m, "PullImage", 5))
            ?? methods.FirstOrDefault(m => Matches(m, "PullImageV2", 3))
            ?? throw new LuckyCaptureException("SDK_LAYOUT_CHANGED", "ToupTek raw pull method is unavailable");
        v4 = pull.GetParameters().Length == 5;
    }

    private static bool Matches(MethodInfo method, string name, int count) {
        var args = method.GetParameters();
        return method.Name == name && args.Length == count && args[0].ParameterType == typeof(ushort[]) &&
            args.Skip(1).Take(count - 2).All(p => p.ParameterType == typeof(int)) && args[^1].IsOut;
    }

    public (bool Success, int Status, uint Width, uint Height) Read(ushort[] pixels, int depth) {
        using var scope = sdkLock.EnterScope();
        object?[] args = v4 ? [pixels, 0, depth, 0, null] : [pixels, depth, null];
        var success = pull.Invoke(sdk, args) is true;
        var status = (int)result.GetValue(sdk)!;
        if (!success) return (false, status, 0, 0);
        var info = args[^1]!;
        var dimensions = info.GetType().GetField("v3")?.GetValue(info) ?? info;
        var width = dimensions.GetType().GetField("width")?.GetValue(dimensions);
        var height = dimensions.GetType().GetField("height")?.GetValue(dimensions);
        if (width is not uint w || height is not uint h)
            throw new LuckyCaptureException("SDK_LAYOUT_CHANGED", "ToupTek frame dimensions are unavailable");
        return (true, status, w, h);
    }

    public static bool IsPending(int status) => status is unchecked((int)0x8000000a) or unchecked((int)0x8001011f);
}
