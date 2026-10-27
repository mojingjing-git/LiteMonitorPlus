using System;
using System.Globalization;
using LiteMonitor.src.Plugins;

if (args.Length < 3)
{
    Console.Error.WriteLine("用法: ThemeMarketValidator <theme.json> <route:gpu|legacy> <material:liquid_glass|cyber_punk|legacy> [declaredSize]");
    return 2;
}

string path = args[0];
string route = args[1];
string material = args[2];
int? declaredSize = null;

if (args.Length >= 4)
{
    if (!int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) || size < 0)
    {
        Console.Error.WriteLine("declaredSize 必须是非负整数");
        return 2;
    }

    declaredSize = size;
}

ThemeMarketValidationResult result = ThemeMarketValidator.ValidateFile(path, route, material, declaredSize);
if (!result.IsValid)
{
    Console.Error.WriteLine(result.ErrorCode + ": " + result.Message);
    return 1;
}

Console.WriteLine("ok");
return 0;
