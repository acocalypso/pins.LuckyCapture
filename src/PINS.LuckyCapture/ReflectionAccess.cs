using System.Reflection;

namespace PINS.LuckyCapture;

/// <summary>Strict, named field access for the owner-authorized experimental connected-camera adapters.</summary>
public static class ReflectionAccess {
    public static T Field<T>(object instance, string name) {
        for (var type = instance.GetType(); type != null; type = type.BaseType) {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field == null) continue;
            if (field.GetValue(instance) is T value) return value;
            throw new LuckyCaptureException("SDK_LAYOUT_CHANGED", $"{type.Name}.{name} has an incompatible value or type");
        }
        throw new LuckyCaptureException("SDK_LAYOUT_CHANGED", $"{instance.GetType().Name}.{name} is unavailable");
    }
}
