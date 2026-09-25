using System.Text.Json;
using DeskNest.Inference;
using Tokenizers.HuggingFace.Tokenizer;

if (args.Length != 2 || args[0] is not ("--inference-worker" or "--tensor-probe" or "--encode-probe"))
{
    Console.Error.WriteLine("Usage: DeskNest.Inference (--inference-worker|--tensor-probe|--encode-probe) MODEL_DIRECTORY");
    return 2;
}
try
{
    if (args[0] == "--inference-worker")
        Probe.Worker(args[1], Console.OpenStandardInput(), Console.OpenStandardOutput());
    else
    {
        using var document = JsonDocument.Parse(Console.OpenStandardInput());
        var root = document.RootElement;
        var request = root.GetProperty("request").Deserialize<Probe.Request>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        if (args[0] == "--encode-probe")
        {
            using var tokenizer = Tokenizer.FromFile(Path.Combine(args[1], "tokenizer.json"));
            using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(args[1], "rl_agent_config.json")));
            Console.WriteLine(JsonSerializer.Serialize(Probe.Encode(request, tokenizer, config.RootElement, args[1]), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        else
        {
            var tensors = root.GetProperty("tensors").Deserialize<Probe.Tensors>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Console.WriteLine(JsonSerializer.Serialize(Probe.Run(request, args[1], tensors), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
    }
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine(e);
    return 1;
}
