using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

class GestorPrincipal
{
    static void Main()
    {
        Console.WriteLine("=== PROGRAMA PRINCIPAL (Gestor) ===");

        // 1) Inicia o Servidor
        Console.WriteLine("[GESTOR] A iniciar Servidor...");
        Process.Start(new ProcessStartInfo
        {
            FileName = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\Servidor 2.0\bin\Debug\net8.0\Servidor 2.0.exe",
            UseShellExecute = true
        });

        // 2) Perguntar quantos Agregadores quer iniciar
        Console.Write("Quantos Agregadores deseja iniciar? ");
        int numAgregadores = int.Parse(Console.ReadLine() ?? "1");

        // 3) Apagar ficheiro antigo (para evitar leituras de configurações velhas)
        string agregadoresFile = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\agregadores_config.txt";
        if (File.Exists(agregadoresFile))
        {
            File.Delete(agregadoresFile);
        }

        // 4) Inicia cada Agregador (que vai escrever no ficheiro)
        for (int i = 1; i <= numAgregadores; i++)
        {
            Console.WriteLine($"[GESTOR] A iniciar Agregador #{i}...");
            Process.Start(new ProcessStartInfo
            {
                FileName = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\Agregador\bin\Debug\net8.0\Agregador.exe",
                UseShellExecute = true
            });
        }

        // 5) Esperar até cada Agregador escrever a sua linha no ficheiro
        Console.WriteLine("[GESTOR] A aguardar que o ficheiro 'agregadores_config.txt' seja criado/preenchido...");

        // Vamos esperar até existirem "numAgregadores" linhas
        while (true)
        {
            if (File.Exists(agregadoresFile))
            {
                var lines = File.ReadAllLines(agregadoresFile);
                if (lines.Length >= numAgregadores)
                {
                    // Já temos pelo menos 1 linha por cada Agregador
                    break;
                }
            }
            Thread.Sleep(1000); // Espera 1 segundo antes de verificar de novo
        }

        // 6) Ler as linhas (ID e Porta) dos Agregadores
        var linhasAgg = File.ReadAllLines(agregadoresFile);
        Console.WriteLine("[GESTOR] Ficheiro lido. Eis os agregadores disponíveis:");
        foreach (var ln in linhasAgg)
        {
            Console.WriteLine("   " + ln);
        }

        // 7) Para cada agregador, perguntar quantas WAVYs de cada tipo
        foreach (var linha in linhasAgg)
        {
            // Formato "AGREGADOR_01|9001"
            var partes = linha.Split('|', StringSplitOptions.TrimEntries);
            string aggId = partes[0];
            string porta = partes[1];

            Console.WriteLine($"\n=== Config para {aggId} (porta {porta}) ===");

            // Pergunta quantas WAVY_Basica
            Console.Write("Quantas WAVY_Basica deseja iniciar? ");
            int numBasicas = int.Parse(Console.ReadLine() ?? "0");
            for (int i = 1; i <= numBasicas; i++)
            {
                Console.WriteLine($"[GESTOR] A iniciar WAVY_Basica #{i} para {aggId}...");
                Console.WriteLine($"Vou iniciar WAVY com estes argumentos: 127.0.0.1 {porta} {aggId}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\Wavy\bin\Debug\net8.0\Wavy.basica.exe",
                    Arguments = $"127.0.0.1 {porta} {aggId}",
                    UseShellExecute = true
                });
            }

            // Pergunta quantas WAVY_Pro
            Console.Write("Quantas WAVY_Pro deseja iniciar? ");
            int numPros = int.Parse(Console.ReadLine() ?? "0");
            for (int i = 1; i <= numPros; i++)
            {
                Console.WriteLine($"[GESTOR] A iniciar WAVY_Pro #{i} para {aggId}...");
                Console.WriteLine($"Vou iniciar WAVY com estes argumentos: 127.0.0.1 {porta} {aggId}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\Wavy.pro\bin\Debug\net8.0\Wavy.pro.exe",
                    Arguments = $"127.0.0.1 {porta} {aggId}",
                    UseShellExecute = true
                });
            }

            // Pergunta quantas WAVY_Meteorologica
            Console.Write("Quantas WAVY_Meteorologica deseja iniciar? ");
            int numMets = int.Parse(Console.ReadLine() ?? "0");
            for (int i = 1; i <= numMets; i++)
            {
                Console.WriteLine($"[GESTOR] A iniciar WAVY_Meteorologica #{i} para {aggId}...");
                Console.WriteLine($"Vou iniciar WAVY com estes argumentos: 127.0.0.1 {porta} {aggId}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\Wavy.metereologica\bin\Debug\net8.0\Wavy.metereologica.exe",
                    Arguments = $"127.0.0.1 {porta} {aggId}",
                    UseShellExecute = true
                });
            }
        }

        Console.WriteLine("[GESTOR] Tudo iniciado!");
        Console.WriteLine("Pressiona ENTER para sair do programa principal...");
        Console.ReadLine();
    }
}
