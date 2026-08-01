using System.Reflection;

namespace CrystalCast.Video;

internal class CefBrowserPolicyProxy : DispatchProxy
{
    private Func<string?, bool>? navigationAllowed;

    public CefBrowserPolicyProxy()
    {
    }

    public static object Create(Type interfaceType, Func<string?, bool> navigationAllowed)
    {
        var proxy = DispatchProxy.Create(interfaceType, typeof(CefBrowserPolicyProxy));
        ((CefBrowserPolicyProxy)proxy).navigationAllowed = navigationAllowed;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null)
            return null;

        args ??= [];
        InitializeOutParameters(targetMethod, args);

        return targetMethod.Name switch
        {
            "OnBeforeBrowse" => ShouldBlockMainFrameNavigation(args),
            "OnOpenUrlFromTab" or "OnBeforePopup" => true,
            "CanDownload" => false,
            "OnBeforeDownload" => true,
            "OnRequestMediaAccessPermission" => DenyWithCallback(args, "Cancel"),
            "OnShowPermissionPrompt" => DenyPermissionPrompt(args),
            _ => DefaultValue(targetMethod.ReturnType),
        };
    }

    private bool ShouldBlockMainFrameNavigation(object?[] args)
    {
        var frame = args.ElementAtOrDefault(2);
        if (frame == null || !GetProperty(frame, "IsMain", false))
            return false;

        var request = args.ElementAtOrDefault(3);
        var url = request == null ? null : GetProperty<string?>(request, "Url", null);
        return navigationAllowed?.Invoke(url) != true;
    }

    private static bool DenyWithCallback(object?[] args, string methodName)
    {
        var callback = args.LastOrDefault();
        callback?.GetType().GetMethod(methodName, Type.EmptyTypes)?.Invoke(callback, null);
        return true;
    }

    private static bool DenyPermissionPrompt(object?[] args)
    {
        var callback = args.LastOrDefault();
        var continueMethod = callback?.GetType().GetMethod("Continue");
        var resultType = continueMethod?.GetParameters().SingleOrDefault()?.ParameterType;
        if (callback != null && continueMethod != null && resultType?.IsEnum == true)
        {
            var deny = Enum.Parse(resultType, "Deny", ignoreCase: true);
            continueMethod.Invoke(callback, [deny]);
        }

        return true;
    }

    private static void InitializeOutParameters(MethodInfo method, object?[] args)
    {
        var parameters = method.GetParameters();
        for (var i = 0; i < Math.Min(parameters.Length, args.Length); i++)
        {
            if (!parameters[i].ParameterType.IsByRef)
                continue;

            var elementType = parameters[i].ParameterType.GetElementType()!;
            args[i] = DefaultValue(elementType);
        }
    }

    private static object? DefaultValue(Type type)
    {
        return type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);
    }

    private static T GetProperty<T>(object target, string propertyName, T fallback)
    {
        var value = target.GetType().GetProperty(propertyName)?.GetValue(target);
        return value is T typed ? typed : fallback;
    }
}
