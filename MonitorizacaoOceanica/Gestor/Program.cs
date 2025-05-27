using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Linq;

class GestorPrincipal
{
    string agregadoresFile = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\agregadores_config.txt";

    static void Main()
    {
        Console.WriteLine("=== PROGRAMA PRINCIPAL (Gestor) ===");

        // 1) Inicia o Servidor (arranca o .exe do Servidor)
        Console.WriteLine("[GESTOR] A iniciar Servidor");
        Process.Start(new ProcessStartInfo
        {
            FileName = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\Servidor 2.0\bin\Debug\net8.0\Servidor 2.0.exe",
            UseShellExecute = true
        });


        // 2) Pergunta quantos Agregadores se deseja iniciar
        Console.Write("Quantos Agregadores deseja iniciar? ");
        int numAgregadores = int.Parse(Console.ReadLine() ?? "1");

        // 3) Apaga o ficheiro agregadores_config.txt antigo, para não ficar com configs velhas
        string agregadoresFile = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\agregadores_config.txt";
        if (File.Exists(agregadoresFile))
        {
            File.Delete(agregadoresFile);
        }

        // 4) Inicia cada Agregador num .exe independente
        for (int i = 1; i <= numAgregadores; i++)
        {
            Console.WriteLine($"[GESTOR] A iniciar Agregador #{i}...");
            Process.Start(new ProcessStartInfo
            {
                FileName = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\Agregador\bin\Debug\net8.0\Agregador.exe",
                UseShellExecute = true
            });
        }

        // 5) Espera até o ficheiro agregadores_config.txt ter 'numAgregadores' linhas
        // Cada Agregador escreve lá "AGREGADOR_XX|<porta>"
        Console.WriteLine("[GESTOR] A aguardar que o ficheiro 'agregadores_config.txt' seja criado/preenchido...");

        while (true)
        {
            if (File.Exists(agregadoresFile))
            {
                var lines = File.ReadAllLines(agregadoresFile);
                if (lines.Length >= numAgregadores)
                {
                    // Temos pelo menos 1 linha por cada agregador
                    break;
                }
            }
            Thread.Sleep(1000); // espera 1 segundo antes de voltar a verificar
        }

        // 6) Lê as linhas do ficheiro => ex. "AGREGADOR_01|8000"
        var linhasAgg = File.ReadAllLines(agregadoresFile);
        Console.WriteLine("[GESTOR] Eis os agregadores disponíveis:");
        foreach (var ln in linhasAgg)
        {
            Console.WriteLine("   " + ln);
        }

        // 7) Para cada agregador, pergunta quantas WAVYs de cada tipo se quer iniciar
        foreach (var linha in linhasAgg)
        {
            // Ex.: "AGREGADOR_01|8000"
            var partes = linha.Split('|', StringSplitOptions.TrimEntries);
            string aggId = partes[0];
            string porta = partes[1];

            Console.WriteLine($"\n=== Config para {aggId} (porta {porta}) ===");

            // WAVY_Basica
            Console.Write("Quantas WAVY_Basica deseja iniciar? ");
            int numBasicas = int.Parse(Console.ReadLine() ?? "0");
            for (int i = 1; i <= numBasicas; i++)
            {
                Console.WriteLine($"[GESTOR] A iniciar WAVY_Basica #{i} para {aggId}...");
                // Indica ao Wavy o IP=127.0.0.1, a Porta e o ID do Agregador
                Process.Start(new ProcessStartInfo
                {
                    FileName = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\Wavy\bin\Debug\net8.0\Wavy.basica.exe",
                    Arguments = $"127.0.0.1 {porta} {aggId}",
                    UseShellExecute = true
                });
            }

            // WAVY_Pro
            Console.Write("Quantas WAVY_Pro deseja iniciar? ");
            int numPros = int.Parse(Console.ReadLine() ?? "0");
            for (int i = 1; i <= numPros; i++)
            {
                Console.WriteLine($"[GESTOR] A iniciar WAVY_Pro #{i} para {aggId}...");
                Process.Start(new ProcessStartInfo
                {
                    FileName = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\Wavy.pro\bin\Debug\net8.0\Wavy.pro.exe",
                    Arguments = $"127.0.0.1 {porta} {aggId}",
                    UseShellExecute = true
                });
            }

            // WAVY_Meteorologica
            Console.Write("Quantas WAVY_Meteorologica deseja iniciar? ");
            int numMets = int.Parse(Console.ReadLine() ?? "0");
            for (int i = 1; i <= numMets; i++)
            {
                Console.WriteLine($"[GESTOR] A iniciar WAVY_Meteorologica #{i} para {aggId}...");
                Process.Start(new ProcessStartInfo
                {
                    FileName = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\Wavy.metereologica\bin\Debug\net8.0\Wavy.metereologica.exe",
                    Arguments = $"127.0.0.1 {porta} {aggId}",
                    UseShellExecute = true
                });
            }
        }

        Console.WriteLine("[GESTOR] Tudo iniciado!");

        // 8) Loop de comandos do Gestor
        while (true)
        {
            Console.WriteLine("\n[GESTOR] Comandos disponíveis:");
            Console.WriteLine("   listar agregadores     => Mostra IDs dos agregadores no config");
            Console.WriteLine("   enviar <AGREGADOR_ID>   => AGREGADOR_ID envia os dados para o servidor ");
            Console.WriteLine("   enviar todos           => Todos os agregadores enviam dados");
            Console.WriteLine("   desligar <AGREGADOR_ID> => Desliga o AGREGADOR_ID");
            Console.WriteLine("   desligar todos         => Desliga todos os Agregadores");
            Console.WriteLine("   desligar.servidor      => Encerra o Servidor (enviando COMANDO TCP ou digitando na consola dele)");
            Console.WriteLine("   exit                   => Fecha este gestor\n");

            Console.Write("[GESTOR]> ");
            string cmd = Console.ReadLine() ?? "";

            if (cmd.ToLower() == "exit")
            {
                Console.WriteLine("[GESTOR] A encerrar o gestor...");
                return;
            }
            else if (cmd.ToLower() == "enviar todos")
            {
                // podes trocar para "sair" ou "desligar" como preferires
                EnviarTodos(agregadoresFile, "enviar");
            }
            else if (cmd.ToLower().StartsWith("enviar "))
            {
                // "enviar AGREGADOR_01"
                string agId = cmd.Substring(7).Trim();
                EnviarComando(agregadoresFile, agId, "enviar");
            }
            else if (cmd.ToLower() == "desligar todos")
            {
                // mandar "sair" a todos
                EnviarTodos(agregadoresFile, "sair");
            }
            else if (cmd.ToLower().StartsWith("desligar "))
            {
                // "desligar AGREGADOR_01"
                string agId = cmd.Substring(8).Trim();
                EnviarComando(agregadoresFile, agId, "sair");
            }
            else if (cmd.ToLower() == "desligar.servidor")
            {
                // Manda "COMANDO | GESTOR | SERVIDOR_01 | desligar_servidor" ao servidor (porta 9000)
                using TcpClient client = new TcpClient("127.0.0.1", 9000);
                NetworkStream stream = client.GetStream();
                string mensagem = "COMANDO | GESTOR | SERVIDOR_01 | desligar_servidor";
                byte[] data = Encoding.UTF8.GetBytes(mensagem);
                stream.Write(data, 0, data.Length);

                // Lê a resposta do Servidor
                byte[] buffer = new byte[1024];
                int read = stream.Read(buffer, 0, buffer.Length);
                string resp = Encoding.UTF8.GetString(buffer, 0, read);
                Console.WriteLine("[GESTOR] Resposta do Servidor: " + resp);
            }
            // Da-me a lista de agregadores disponíveis
            else if (cmd.ToLower() == "listar agregadores")
            {
                if (!File.Exists(agregadoresFile))
                {
                    Console.WriteLine("[GESTOR] Não existe ficheiro agregadores_config.txt. Nenhum agregador ativo.");
                    return;
                }

                var linhas = File.ReadAllLines(agregadoresFile);
                if (linhas.Length == 0)
                {
                    Console.WriteLine("[GESTOR] Ficheiro está vazio. Nenhum agregador disponível.");
                    return;
                }

                Console.WriteLine("[GESTOR] Agregadores disponíveis:");
                foreach (var linha in linhas)
                {
                    // Cada linha deve ter: "AGREGADOR_XX|PORTA"
                    var partes = linha.Split('|', StringSplitOptions.TrimEntries);
                    if (partes.Length >= 2)
                    {
                        string agId = partes[0];
                        string porta = partes[1];
                        Console.WriteLine($"   ID: {agId}, Porta: {porta}");
                    }
                    else
                    {
                        Console.WriteLine($"   [FORMATO INVÁLIDO]: {linha}");
                    }
                }
            }
            else
            {
                Console.WriteLine("[GESTOR] Comando desconhecido: " + cmd);
            }
        }
    }

    /// <summary>
    /// Envia um comando (ex. "enviar", "sair") a TODOS os agregadores que constam
    /// em 'agregadores_config.txt'.
    /// </summary>
    static void EnviarTodos(string agregadoresFile, string acao)
    {
        var linhas = File.ReadAllLines(agregadoresFile);
        foreach (var linha in linhas)
        {
            var partes = linha.Split('|', StringSplitOptions.TrimEntries);
            string agId = partes[0];
            EnviarComando(agregadoresFile, agId, acao);
        }
    }

    /// <summary>
    /// Envia um comando (ex. "enviar", "sair") a um agregador específico (agId).
    /// Lê 'agregadores_config.txt' para descobrir a porta.
    /// </summary>
    static void EnviarComando(string agregadoresFile, string agId, string acao)
    {
        // Descobre a porta do Agregador no ficheiro
        var linhas = File.ReadAllLines(agregadoresFile);
        string portaEncontrada = null;
        foreach (var l in linhas)
        {
            var partes = l.Split('|', StringSplitOptions.TrimEntries);
            if (partes[0] == agId)
            {
                portaEncontrada = partes[1];
                break;
            }
        }
        if (portaEncontrada == null)
        {
            Console.WriteLine($"[GESTOR] ERRO: Agregador {agId} não encontrado em {agregadoresFile}.");
            return;
        }

        int port = int.Parse(portaEncontrada);
        try
        {
            // Liga-se por TCP ao Agregador
            using TcpClient client = new TcpClient("127.0.0.1", port);
            NetworkStream stream = client.GetStream();

            // Ex.: "COMANDO | GESTOR | AGREGADOR_01 | enviar"
            string mensagem = $"COMANDO | GESTOR | {agId} | {acao}";
            byte[] data = Encoding.UTF8.GetBytes(mensagem);
            stream.Write(data, 0, data.Length);

            // Lê a resposta do Agregador
            byte[] buffer = new byte[1024];
            int read = stream.Read(buffer, 0, buffer.Length);
            string resp = Encoding.UTF8.GetString(buffer, 0, read);
            Console.WriteLine($"[GESTOR] Resposta de {agId}: {resp}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GESTOR] Erro ao ligar a {agId}: {ex.Message}");
        }
    }
}
