using System.Text.Json;
using Quasar;

if (args is not [var baseline, var target, var output])
{
    Console.Error.WriteLine("Usage: PatchTool <previous-release-directory> <target-release-directory> <patch-zip-output>");
    return 2;
}
try
{
    var patch = RuntimePatch.Create(baseline, target, output);
    Console.WriteLine(JsonSerializer.Serialize(patch, Storage.Json));
    return 0;
}
catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
