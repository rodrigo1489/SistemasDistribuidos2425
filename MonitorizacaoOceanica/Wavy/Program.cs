using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using RabbitMQ.Client;


class WavyBasica
{
    // Identificador único desta WAVY e o seu tipo
    static string wavyId;
    static string tipoWavy = "WAVY_Básica";

    // Flag para indicar se está ativa
    static bool ativo = true;

    // Ficheiro onde registamos todos os IDs de WAVYs criados
    static string configFile = @"C:\\Users\\rodri\\source\\repos\\SistemasDistribuidos2425.2\\MonitorizacaoOceanica\\wavys_config.txt";

    // Dados do Agregador (IP, Porta, e ID do Agregador)
    static string agregadorIp;
    static int agregadorPorta;
    static string agregadorId;

    // Contador de erros de envio consecutivos
    // e limite máximo antes de desligar a WAVY
    static int erroEnvioCount = 0;
    static int erroEnvioMax = 10;

    static void Main(string[] args)
    {
        // Se for interrompida com Ctrl+C, envia aviso de desligar, remove do ficheiro e fecha
        Console.CancelKeyPress += (sender, e) => {
            e.Cancel = true;
            EnviarAvisoDesligar();
            RemoverRegisto();
            Environment.Exit(0);
        };

        // Gera um ID único do formato WAVY_01, WAVY_02, etc.
        wavyId = GerarWavyIdDisponivel();
        // Escreve esse ID no ficheiro de WAVYs
        File.AppendAllText(configFile, wavyId + "\n");

        // Se receber 3 argumentos (ip, porta, agregadorId), usa-os diretamente
        // caso contrário, escolhe um Agregador do ficheiro agregadores_config.txt
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

        // Envia mensagem REGISTO ao Agregador
        RegistarWavy(agregadorIp, agregadorPorta, agregadorId);

        // Marca estado "associada" no Agregador (ficheiro estado_wavys.txt)
        EnviarEstado("associada");

        // Cria uma thread que envia dados de minuto a minuto
        new Thread(() => EnvioPeriodico(agregadorIp, agregadorPorta)).Start();

        // Loop principal para comandos manuais (enviar manual, desligar, estado X)
        while (true)
        {
            Console.WriteLine($"[{wavyId}] Comando ('manual', 'desligar' ou 'estado <NOVO_ESTADO>'):");
            string comando = Console.ReadLine();

            if (comando == "manual")
            {
                // Envio manual de dados
                PublicarDadosRabbit();
            }
            else if (comando == "desligar")
            {
                // Desligar manualmente
                ativo = false;
                EnviarAvisoDesligar();
                RemoverRegisto();
                Environment.Exit(0);
            }
            else if (comando.StartsWith("estado "))
            {
                // "estado operação", "estado manutenção", etc.
                string novoEstado = comando.Substring(7).Trim();
                EnviarEstado(novoEstado);
            }
        }
    }

    /// Gera um ID automático único (WAVY_01, WAVY_02, etc.).
    /// Lê o ficheiro configFile para não repetir IDs.
    static string GerarWavyIdDisponivel()
    {
        int contador = 1;
        if (!File.Exists(configFile)) File.WriteAllText(configFile, "");
        var existentes = File.ReadAllLines(configFile);
        // Procura o primeiro WAVY_{XX} que não existe no ficheiro
        while (existentes.Contains($"WAVY_{contador:D2}")) contador++;
        return $"WAVY_{contador:D2}";
    }

    /// Lê o ficheiro agregadores_config.txt e deixa o utilizador escolher um.
    /// Retorna (ip, porta, agregadorId).
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
        // Normalmente IP=127.0.0.1, Porta=int.Parse(partes[1]), ID=partes[0]
        return ("127.0.0.1", int.Parse(partes[1]), partes[0]);
    }

    /// Envia a mensagem "REGISTO | wavyId | agregadorId | WAVY_Básica" ao Agregador.
    static void RegistarWavy(string ip, int porta, string aggId)
    {
        TcpClient client = new TcpClient(ip, porta);
        NetworkStream stream = client.GetStream();
        string msg = $"REGISTO | {wavyId} | {aggId} | {tipoWavy}";
        stream.Write(Encoding.UTF8.GetBytes(msg));

        // Lê a resposta (CONFIRMADO / ERRO)
        byte[] buffer = new byte[1024];
        int bytesRead = stream.Read(buffer, 0, buffer.Length);
        Console.WriteLine($"[{wavyId}] Registo: {Encoding.UTF8.GetString(buffer, 0, bytesRead)}");
        client.Close();
    }

    /// Thread que a cada 60s lê o estado no Agregador. Se for "operação" ou "associada", envia dados.
    /// Caso seja "manutenção" ou outro, pausa o envio (imprime aviso).
    static void EnvioPeriodico(string ip, int porta)
    {
        while (ativo)
        {
            string estado = ObterEstadoDaWavyNoAgregador();

            if (estado == "operação")
            {
                PublicarDadosRabbit();
            }
            else if (estado == "associada")
            {
                // Envia no arranque
                PublicarDadosRabbit();
            }
            else
            {
                Console.WriteLine($"[{wavyId}] Estado atual no agregador é '{estado}' — envio automático pausado.");
            }

            // Espera 1 minuto
            Thread.Sleep(60000);
        }
    }

    /// Envia dados da WAVY_Básica -> apenas Temperatura
    static void PublicarDadosRabbit()
    {
        var rand = new Random();
        double valor = rand.Next(18, 26) + rand.NextDouble();
        string timestamp = DateTime.UtcNow.ToString("o");
        // CSV: timestamp,valor
        string payload = $"{timestamp},{valor:F1}";

        var factory = new ConnectionFactory { HostName = "localhost" };
        using var conn = factory.CreateConnection();
        using var channel = conn.CreateModel();

        channel.ExchangeDeclare("wavys_data", ExchangeType.Topic, durable: true);

        string routingKey = $"wavy.{wavyId}.csv";
        var body = Encoding.UTF8.GetBytes(payload);

        channel.BasicPublish("wavys_data", routingKey, null, body);
        Console.WriteLine($"[{wavyId}] Pub csv: {payload}");
    }



    /// Envia "ESTADO | wavyId | agregadorId | <novoEstado>" (ex.: "manutenção", "operação") 
    /// para o Agregador atualizar no estado_wavys.txt
    static void EnviarEstado(string novoEstado)
    {
        string msg = $"ESTADO | {wavyId} | {agregadorId} | {novoEstado}";
        try
        {
            using TcpClient client = new TcpClient(agregadorIp, agregadorPorta);
            NetworkStream stream = client.GetStream();
            stream.Write(Encoding.UTF8.GetBytes(msg));

            // Lê resposta
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

    /// Envia "DESLIGAR | wavyId | agregadorId | timestamp" ao Agregador,
    /// indicando que esta Wavy se vai encerrar voluntariamente.
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

    /// Remove o ID desta WAVY do ficheiro wavys_config.txt.
    /// Assim não fica lá duplicado ou "fantasma" quando a Wavy encerrar.
    static void RemoverRegisto()
    {
        if (!File.Exists(configFile)) return;
        var linhas = File.ReadAllLines(configFile).Where(l => l != wavyId).ToList();
        File.WriteAllLines(configFile, linhas);
    }

    /// Lê o estado desta WAVY no ficheiro 'estado_wavys.txt' (operação, manutenção, associada, etc.)
    /// Se não encontrar nada, assume 'operação'.
    static string ObterEstadoDaWavyNoAgregador()
    {
        string estadoPath = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\estado_wavys.txt";

        // Se não existir o ficheiro, assumimos "operação"
        if (!File.Exists(estadoPath))
            return "operação";

        try
        {
            // Exemplo de linhas no ficheiro:
            // "WAVY_01:operação:AGREGADOR_01"
            // "WAVY_02:desligada:AGREGADOR_02"
            var linha = File.ReadLines(estadoPath)
                            .FirstOrDefault(l => l.StartsWith(wavyId + ":"));
            if (linha != null)
            {
                var partes = linha.Split(':');
                // parted[0] = "WAVY_01", parted[1] = "operação", parted[2] = "AGREGADOR_01"
                if (partes.Length >= 2)
                {
                    // parted[1] deve ser o estado
                    string estado = partes[1].Trim().ToLower();

                    // Se o estado for "desligada", a WAVY encerra
                    if (estado == "desligada")
                    {
                        Console.WriteLine($"[{wavyId}] Estado 'desligada' detetado. A encerrar...");
                        EnviarAvisoDesligar();  // se ainda quiseres avisar o agregador (pode falhar se já estiver desligado)
                        RemoverRegisto();
                        Environment.Exit(0);
                    }

                    // Se não está 'desligada', devolve o estado
                    return estado;
                }
            }
        }
        catch
        {
            // Se houver algum erro de IO, assumimos operação
        }

        // Fallback se não achamos nada
        return "operação";
    }
}
