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

// Единый appsettings.json на весь конвейер — читаем СВОЮ секцию «MLTextToData»;
// общий корень рабочих данных (WorkRoot) лежит на верхнем уровне файла.
// Файл ищем рядом с исполняемым файлом: приложения конвейера лежат в одном каталоге
// и запускаются из произвольного рабочего каталога.
var fullConfig = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();
var workRoot = WorkDir.GetRoot(fullConfig);
var config = fullConfig.GetSection("MLTextToData");

// Обязательные пути; «!» оправдан — отсутствие ключа должно валить запуск сразу.
// Рабочие данные — под общим корнем works/…; модель НЕ трогаем: data/model.zip —
// часть поставки и лежит рядом с приложением, а не среди рабочих данных.
var inputRoot = WorkDir.Resolve(workRoot, config["InputRoot"]!);
var cellsOutput = WorkDir.Resolve(workRoot, config["CellsOutputRoot"]!);
var otherOutput = WorkDir.Resolve(workRoot, config["NonCellsOutputRoot"]!);
var dbPath = WorkDir.Resolve(workRoot, config["DatabasePath"]!);
var modelPath = config["ModelPath"]!;
var parallelDegree = int.Parse(config["ParallelDegree"] ?? "4");
var threshold = config.GetValue<float>("PredictionThreshold");

using var repo = new LiteDbRepository(dbPath);
var scanner = new FileScanner(inputRoot);
var extractor = new TextExtractor();
var hashService = new HashService();
var trainer = new MlTrainer(modelPath);
var predictor = new MlPredictor(modelPath);

var trainCmd = new TrainCommand(repo, trainer, predictor);
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

    // label <каталог> <cells|other> — массовая разметка обучающей выборки
    case "label":
        if (args.Length < 3 || (args[2] != "cells" && args[2] != "other"))
        {
            Log.Error("Использование: label <каталог> <cells|other>");
            break;
        }

        await new LabelCommand(hashService, repo).RunAsync(args[1], args[2] == "cells");
        break;

    default:
        Log.Info("Команды: train | process | label <каталог> <cells|other>");
        break;
}
