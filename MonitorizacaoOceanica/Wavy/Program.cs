using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;

class WavyBasica
{
    static string wavyId;
    static string tipoWavy = "WAVY_Básica";
    static bool ativo = true;
    static string configFile = @"C:\\Users\\rodri\\source\\repos\\SistemasDistribuidos2425.2\\MonitorizacaoOceanica\\wavys_config.txt";
    static string agregadorIp;
    static int agregadorPorta;
    static string agregadorId;

    static void Main(string[] args)
    {
        Console.CancelKeyPress += (sender, e) => {
            e.Cancel = true;
            EnviarAvisoDesligar();
            RemoverRegisto();
            Environment.Exit(0);
        };

        wavyId = GerarWavyIdDisponivel();
        File.AppendAllText(configFile, wavyId + "\n");

        // Se tiver 3 argumentos, usa-os; caso contrário, escolhe agregador
        if (args.Length == 3)
        {
            agregadorIp = args[0];
            agregadorPorta = int.Parse(args[1]);
            agregadorId = args[2];
            Console.WriteLine($"[{wavyId}] Recebidos argumentos -> IP={agregadorIp}, Porta={agregadorPorta}, AgregadorID={agregadorId}");
        }
        else
        {
            (agregadorIp, agregadorPorta, agregadorId) = EscolherAgregador();
        }

        RegistarWavy(agregadorIp, agregadorPorta, agregadorId);

        // Basta dizer que estamos "associada" no arranque => manda estado ao Agregador
        EnviarEstado("associada");

        // Thread que envia temperatura de minuto a minuto
        new Thread(() => EnvioPeriodico(agregadorIp, agregadorPorta)).Start();

        while (true)
        {
            Console.WriteLine($"[{wavyId}] Comando ('manual', 'desligar' ou 'estado <NOVO_ESTADO>'):");
            string comando = Console.ReadLine();
            if (comando == "manual")
            {
                EnviarDados(agregadorIp, agregadorPorta);
            }
            else if (comando == "desligar")
            {
                ativo = false;
                EnviarAvisoDesligar();
                RemoverRegisto();
                Environment.Exit(0);
            }
            else if (comando.StartsWith("estado "))
            {
                string novoEstado = comando.Substring(7).Trim();
                EnviarEstado(novoEstado);
            }
        }
    }

    static string GerarWavyIdDisponivel()
    {
        int contador = 1;
        if (!File.Exists(configFile)) File.WriteAllText(configFile, "");
        var existentes = File.ReadAllLines(configFile);
        while (existentes.Contains($"WAVY_{contador:D2}")) contador++;
        return $"WAVY_{contador:D2}";
    }

    static (string, int, string) EscolherAgregador()
    {
        string agregadoresFile = @"C:\\Users\\rodri\\source\\repos\\SistemasDistribuidos2425.2\\MonitorizacaoOceanica\\agregadores_config.txt";
        if (!File.Exists(agregadoresFile))
        {
            Console.WriteLine("Nenhum agregador disponível.");
            Environment.Exit(0);
        }
        var linhas = File.ReadAllLines(agregadoresFile);
        Console.WriteLine("Agregadores disponíveis:");
        for (int i = 0; i < linhas.Length; i++)
            Console.WriteLine($"[{i}] {linhas[i]}");

        int escolha = -1;
        while (escolha < 0 || escolha >= linhas.Length)
        {
            Console.Write("Escolha o número do agregador: ");
            int.TryParse(Console.ReadLine(), out escolha);
        }

        var partes = linhas[escolha].Split('|');
        return ("127.0.0.1", int.Parse(partes[1]), partes[0]);
    }

    static void RegistarWavy(string ip, int porta, string aggId)
    {
        TcpClient client = new TcpClient(ip, porta);
        NetworkStream stream = client.GetStream();
        string msg = $"REGISTO | {wavyId} | {aggId} | {tipoWavy}";
        stream.Write(Encoding.UTF8.GetBytes(msg));
        byte[] buffer = new byte[1024];
        int bytesRead = stream.Read(buffer, 0, buffer.Length);
        Console.WriteLine($"[{wavyId}] Registo: {Encoding.UTF8.GetString(buffer, 0, bytesRead)}");
        client.Close();
    }

    static void EnvioPeriodico(string ip, int porta)
    {
        while (ativo)
        {
            string estado = ObterEstadoDaWavyNoAgregador();

            if (estado == "operação")
            {
                EnviarDados(ip, porta);
            }
            else if (estado == "associada")
            {
                EnviarDados(ip, porta);
            }
            else
            {
                Console.WriteLine($"[{wavyId}] Estado atual no agregador é '{estado}' — envio automático pausado.");
            }

            Thread.Sleep(60000);
        }
    }

    static void EnviarDados(string ip, int porta)
    {
        Random rand = new Random();
        double valor = rand.Next(18, 26) + rand.NextDouble();
        string timestamp = DateTime.UtcNow.ToString("o");
        string msg = $"DADOS | {wavyId} | {agregadorId} | Temperatura | {valor:F1} | {timestamp}";

        try
        {
            using TcpClient client = new TcpClient(ip, porta);
            NetworkStream stream = client.GetStream();
            stream.Write(Encoding.UTF8.GetBytes(msg));
            byte[] buffer = new byte[1024];
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            Console.WriteLine($"[{wavyId}] Temperatura enviada: {Encoding.UTF8.GetString(buffer, 0, bytesRead)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{wavyId}] Erro ao enviar: {ex.Message}");
        }
    }

    static void EnviarEstado(string novoEstado)
    {
        // Exemplo: ESTADO | WAVY_01 | AGREGADOR_01 | manutenção
        string msg = $"ESTADO | {wavyId} | {agregadorId} | {novoEstado}";

        try
        {
            using TcpClient client = new TcpClient(agregadorIp, agregadorPorta);
            NetworkStream stream = client.GetStream();
            stream.Write(Encoding.UTF8.GetBytes(msg));
            byte[] buffer = new byte[1024];
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            string resp = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            Console.WriteLine($"[{wavyId}] Estado enviado: {resp}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{wavyId}] Erro ao enviar estado: {ex.Message}");
        }
    }

    static void EnviarAvisoDesligar()
    {
        string timestamp = DateTime.UtcNow.ToString("o");
        string msg = $"DESLIGAR | {wavyId} | {agregadorId} | {timestamp}";

        try
        {
            using TcpClient client = new TcpClient(agregadorIp, agregadorPorta);
            NetworkStream stream = client.GetStream();
            stream.Write(Encoding.UTF8.GetBytes(msg));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{wavyId}] Erro ao enviar aviso de desligar: {ex.Message}");
        }
    }

    static void RemoverRegisto()
    {
        if (!File.Exists(configFile)) return;
        var linhas = File.ReadAllLines(configFile).Where(l => l != wavyId).ToList();
        File.WriteAllLines(configFile, linhas);
    }
    static string ObterEstadoDaWavyNoAgregador()
    {
        string estadoPath = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\estado_wavys.txt";

        if (!File.Exists(estadoPath))
            return "operação"; // se ficheiro não existir, assume ativo

        try
        {
            var linha = File.ReadLines(estadoPath)
                            .FirstOrDefault(l => l.StartsWith(wavyId + ":"));

            if (linha != null)
            {
                var partes = linha.Split(':');
                if (partes.Length >= 2)
                    return partes[1].Trim().ToLower(); // estado
            }
        }
        catch { }

        return "operação"; // default seguro
    }
}
