// MLTextToData — третий этап пайплайна: бинарный ML-классификатор, отделяющий
// документы про базовые станции сотовой связи от посторонних СЭЗ-заключений
// (склады, производства и т.п.). Вход — тексты из ParseHTML, выход — копии,
// разложенные по каталогам CellsOutputRoot / NonCellsOutputRoot.
//
// Команды: train — переобучить модель по размеченной выборке (принудительно);
//          process — дообучить при необходимости и классифицировать файлы.
using CellsClassifier.Commands;
using CellsClassifier.Services;
using GetSiteData.Common;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

// Обязательные пути; «!» оправдан — отсутствие ключа должно валить запуск сразу.
var inputRoot = config["InputRoot"]!;
var cellsOutput = config["CellsOutputRoot"]!;
var otherOutput = config["NonCellsOutputRoot"]!;
var dbPath = config["DatabasePath"]!;
var modelPath = config["ModelPath"]!;
var parallelDegree = int.Parse(config["ParallelDegree"] ?? "4");
var threshold = config.GetValue<float>("PredictionThreshold");

using var repo = new LiteDbRepository(dbPath);
var scanner = new FileScanner(inputRoot);
var extractor = new TextExtractor();
var hashService = new HashService();
var trainer = new MlTrainer(modelPath);
var predictor = new MlPredictor(modelPath);

var trainCmd = new TrainCommand(repo, trainer);
var processCmd = new ProcessCommand(
    scanner,
    extractor,
    hashService,
    repo,
    predictor,
    cellsOutput,
    otherOutput,
    parallelDegree,
    threshold);

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "train";

switch (command)
{
    case "train":
        await trainCmd.RunAsync(force: true);
        break;

    case "process":
        await trainCmd.RunAsync(force: false);
        await processCmd.RunAsync();
        break;

    default:
        Log.Info("Команды: train | process");
        break;
}
