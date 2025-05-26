using RabbitMQ.Client;
using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;

class WavyMeteorologica
{
    // Identificação desta WAVY e seu tipo (Meteorológica)
    static string wavyId;
    static string tipoWavy = "WAVY_Meteorológica";

    // Flag que indica se a WAVY ainda está ativa
    static bool ativo = true;

    // Ficheiro onde registamos o ID de cada Wavy que for iniciada
    static string configFile = @"C:\\Users\\rodri\\source\\repos\\SistemasDistribuidos2425.2\\MonitorizacaoOceanica\\wavys_config.txt";

    // Dados de ligação (IP, Porta, e ID) do Agregador a que se liga
    static string agregadorIp;
    static int agregadorPorta;
    static string agregadorId;

    // Contador de erros consecutivos ao enviar dados
    // Se atingir erroEnvioMax, a WAVY desliga-se
    static int erroEnvioCount = 0;
    static int erroEnvioMax = 10;

    static void Main(string[] args)
    {
        // Se for forçado a fechar (Ctrl+C), envia aviso de desligar e remove-se do ficheiro
        Console.CancelKeyPress += (sender, e) => {
            e.Cancel = true;
            EnviarAvisoDesligar();
            RemoverRegisto();
            Environment.Exit(0);
        };

        // Gera um ID único (ex.: "WAVY_01", "WAVY_02", etc.)
        wavyId = GerarWavyIdDisponivel();
        // Acrescenta esse ID ao ficheiro de Wavys
        File.AppendAllText(configFile, wavyId + "\n");

        // Se foram passados 3 argumentos (IP, Porta, AgregadorID), usa-os;
        // caso contrário, pergunta ao utilizador qual Agregador quer usar
        if (args.Length == 3)
        {
            agregadorIp = args[0];
            agregadorPorta = int.Parse(args[1]);
            agregadorId = args[2];
            Console.WriteLine($"[{wavyId}] Recebidos argumentos -> IP={agregadorIp}, Porta={agregadorPorta}, AgregadorID={agregadorId}");
        }
        else
        {
            // Escolhe manualmente (ler agregadores_config.txt)
            (agregadorIp, agregadorPorta, agregadorId) = EscolherAgregador();
        }

        // Faz o registo no Agregador => "REGISTO | wavyId | agregadorId | WAVY_Meteorológica"
        RegistarWavy(agregadorIp, agregadorPorta, agregadorId);

        // Envia estado "associada" ao Agregador (ficará em estado_wavys.txt)
        EnviarEstado("associada");

        // Cria uma thread que periodicamente envia dados meteorológicos
        new Thread(() => EnvioPeriodico(agregadorIp, agregadorPorta)).Start();

        // Loop principal de consola: comanda "manual", "desligar", "estado <X>"
        while (true)
        {
            Console.WriteLine($"[{wavyId}] Comando ('manual', 'desligar' ou 'estado <NOVO_ESTADO>'):");
            string comando = Console.ReadLine();
            if (comando == "manual")
            {
                // Envio manual de dados (Temperatura, Vento, Humidade)
                PublicarDadosRabbitMeteorologica();
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
                // Mudar o estado
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
        while (existentes.Contains($"WAVY_{contador:D2}")) contador++;
        return $"WAVY_{contador:D2}";
    }

    /// Escolhe um agregador perguntando ao utilizador. Lê o ficheiro agregadores_config.txt,
    /// lista as opções e espera que o user escolha uma.
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
        {
            Console.WriteLine($"[{i}] {linhas[i]}");
        }

        int escolha = -1;
        while (escolha < 0 || escolha >= linhas.Length)
        {
            Console.Write("Escolha o número do agregador: ");
            int.TryParse(Console.ReadLine(), out escolha);
        }

        var partes = linhas[escolha].Split('|');
        // Retorna IP=127.0.0.1, porta=partes[1], ID=partes[0]
        return ("127.0.0.1", int.Parse(partes[1]), partes[0]);
    }

    /// Envia a mensagem de registo para o Agregador: "REGISTO | wavyId | agregadorId | WAVY_Meteorológica".
    static void RegistarWavy(string ip, int porta, string aggId)
    {
        using TcpClient client = new TcpClient(ip, porta);
        NetworkStream stream = client.GetStream();
        string msg = $"REGISTO | {wavyId} | {aggId} | {tipoWavy}";
        stream.Write(Encoding.UTF8.GetBytes(msg));
        byte[] buffer = new byte[1024];
        int bytesRead = stream.Read(buffer, 0, buffer.Length);
        Console.WriteLine($"[{wavyId}] Registo: {Encoding.UTF8.GetString(buffer, 0, bytesRead)}");
    }

    /// Thread que, enquanto 'ativo', a cada 60 segundos verifica o estado
    /// (operação, associada, manutenção, etc.) e se for "operação" ou "associada",
    /// envia dados meteorológicos.

    static void EnvioPeriodico(string ip, int porta)
    {
        while (ativo)
        {
            string estado = ObterEstadoDaWavyNoAgregador();

            if (estado == "operação")
            {
                PublicarDadosRabbitMeteorologica();
            }
            else if (estado == "associada")
            {
                // podes enviar no arranque 
                PublicarDadosRabbitMeteorologica();
            }
            else
            {
                Console.WriteLine($"[{wavyId}] Estado atual no agregador é '{estado}' — envio automático pausado.");
            }

            Thread.Sleep(60000); // 1 minuto
        }
    }

    /// WAVY Meteorológica -> envia (Temperatura, Velocidade do Vento, Humidade) de cada vez.
    static void PublicarDadosRabbitMeteorologica()
    {
        // Gera valores aleatórios
        var rand = new Random();
        double temp = rand.Next(18, 26) + rand.NextDouble();
        double vento = rand.Next(0, 30) + rand.NextDouble();
        double hum = rand.Next(30, 100) + rand.NextDouble();
        string timestamp = DateTime.UtcNow.ToString("o");

        // Monta o XML
        string payload =
          $"<d>" +
            $"<ts>{timestamp}</ts>" +
            $"<t>{temp:F1}</t>" +
            $"<v>{vento:F1}</v>" +
            $"<h>{hum:F1}</h>" +
          $"</d>";

        // Publica no RabbitMQ
        var factory = new ConnectionFactory { HostName = "localhost" };
        using var conn = factory.CreateConnection();
        using var channel = conn.CreateModel();
        channel.ExchangeDeclare("wavys_data", ExchangeType.Topic, durable: true);

        // Routing key inclui o tipo de ficheiro
        string routingKey = $"wavy.{wavyId}.xml";
        var body = Encoding.UTF8.GetBytes(payload);
        channel.BasicPublish("wavys_data", routingKey, null, body);

        Console.WriteLine($"[{wavyId}] Pub xml: {payload}");
    }


    /// Envia de facto cada tipo de sensor, construindo a string "DADOS | wavyId | agregadorId | tipo | valor | timestamp"
    /// Tenta conectar ao Agregador e ler a resposta. Se falhar, incrementa erroEnvioCount.
    /// Se atingir erroEnvioMax, desliga-se.
    static void Enviar(string ip, int porta, string tipo, int min, int max)
    {
        Random rand = new Random();
        double valor = rand.Next(min, max) + rand.NextDouble();
        string timestamp = DateTime.UtcNow.ToString("o");
        string msg = $"DADOS | {wavyId} | {agregadorId} | {tipo} | {valor:F1} | {timestamp}";

        try
        {
            using TcpClient client = new TcpClient(ip, porta);
            NetworkStream stream = client.GetStream();
            stream.Write(Encoding.UTF8.GetBytes(msg));

            byte[] buffer = new byte[1024];
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            Console.WriteLine($"[{wavyId}] {tipo} enviada: {Encoding.UTF8.GetString(buffer, 0, bytesRead)}");

            // Se chegou aqui sem erro, zera o contador
            erroEnvioCount = 0;
        }
        catch (Exception ex)
        {
            erroEnvioCount++;
            Console.WriteLine($"[{wavyId}] Erro ao enviar (contagem {erroEnvioCount}/{erroEnvioMax}): {ex.Message}");

            if (erroEnvioCount >= erroEnvioMax)
            {
                // Atingiu número máximo de falhas
                Console.WriteLine($"[{wavyId}] Atingiu {erroEnvioCount} erros de envio. A desligar...");
                EnviarAvisoDesligar();  // tenta ainda avisar
                RemoverRegisto();
                Environment.Exit(0);
            }
        }
    }

    /// Envia "ESTADO | wavyId | agregadorId | novoEstado" para o Agregador,
    /// informando que se mudou para operação, manutenção, etc.
    static void EnviarEstado(string novoEstado)
    {
        string msg = $"ESTADO | {wavyId} | {agregadorId} | {novoEstado}";
        try
        {
            using TcpClient client = new TcpClient(agregadorIp, agregadorPorta);
            NetworkStream stream = client.GetStream();
            stream.Write(Encoding.UTF8.GetBytes(msg));

            // Lê a resposta
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
    /// avisando que esta WAVY está a encerrar.
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

    /// Remove o wavyId do ficheiro wavys_config.txt quando esta WAVY se desliga,
    /// para não duplicar IDs no futuro.
    static void RemoverRegisto()
    {
        if (!File.Exists(configFile)) return;
        var linhas = File.ReadAllLines(configFile).Where(l => l != wavyId).ToList();
        File.WriteAllLines(configFile, linhas);
    }

    /// Lê o ficheiro estado_wavys.txt para descobrir se o Agregador marcou
    /// esta WAVY como operação, manutenção, etc. Se não encontrar, assume "operação".
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
