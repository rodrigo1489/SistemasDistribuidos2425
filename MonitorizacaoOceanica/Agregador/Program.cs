using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Xml.Linq;
using Google.Protobuf;
using Grpc.Net.Client;
using Preprocess;    // namespace gerado a partir do preprocess.proto
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

class Agregador
{
    // No início da tua classe Agregador:
    // Dentro da classe Agregador, antes de Main:
    static readonly object _wavysLock = new object();
    static readonly HashSet<string> _wavys = new HashSet<string>();


    // Cria um canal e um client gRPC para o serviço de pré-processamento
    private static readonly GrpcChannel _preprocessChannel =
        GrpcChannel.ForAddress("http://localhost:5001");
    private static readonly PreprocessingService.PreprocessingServiceClient
        _preprocessClient = new(_preprocessChannel);

    // Identificador e porta do Agregador
    static string agregadorId = string.Empty;
    static int porta;

    // Ficheiro global de config (ID|PORTA) para saber que Agregadores existem
    static string configFile = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\agregadores_config.txt";

    // Estado das WAVYs - "WAVY_01:operação::2025-04-20T10:00:00Z"
    static string wavysEstadoFile = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\estado_wavys.txt";

    static TcpListener? listener;

    // Base de onde os ficheiros são criados
    static string basePath = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\agregador_data\";

    // Retorna o caminho para um tipo de dado com base no agregador atual
    static string CaminhoFicheiro(string tipo)
    {
        return Path.Combine(basePath, $"{tipo}_{agregadorId}.txt");
    }

    static Mutex instanceMutex = null!;

    // Dicionário para armazenar Mutexes por caminho de ficheiro
    static Dictionary<string, Mutex> ficheiroMutexes = new Dictionary<string, Mutex>();
    // Para proteger o dicionário acima
    static object ficheiroMutexesLock = new object();





    static void Main()
    {
        // Gera ID e porta livres
        (agregadorId, porta) = GerarAgregadorDisponivel();
        Console.WriteLine($"[INFO] Este agregador vai correr como {agregadorId} na porta {porta}.");

        string nomeUnico = $"mutex_instancia_{agregadorId}";
        bool created;

        instanceMutex = new Mutex(true, nomeUnico, out created);
        if (!created)
        {
            Console.WriteLine("Já existe uma instância deste Agregador a correr.");
            return;
        }

        // Ctrl+C => remove registo e envia DESLIGAR
        Console.CancelKeyPress += (sender, e) => {
            e.Cancel = true;
            RemoverRegistoAgregador();
            EnviarAvisoDesligarAoServidor();
            Console.WriteLine($"[{agregadorId}] Registo removido por encerramento forçado.");
            Environment.Exit(0);
        };


        // Adiciona este Agregador no ficheiro global
        File.AppendAllText(configFile, $"{agregadorId}|{porta}\n");



        // escolha dos sensores
        Console.WriteLine("Que sensores queres subscrever via RabbitMQ? (separa por vírgula)");
        Console.WriteLine("1) Temperatura   2) Pressão   3) Velocidade do Vento   4) Humidade");
        var input = Console.ReadLine() ?? "";
        var sensores = input
          .Split(',', StringSplitOptions.RemoveEmptyEntries)
          .Select(s => s.Trim())
          .Where(s => s == "1" || s == "2" || s == "3" || s == "4")
          .Select(s => s switch {
              "1" => "Temperatura",
              "2" => "Pressão",
              "3" => "Velocidade do Vento",
              "4" => "Humidade",
              _ => ""
          })
          .Where(s => !string.IsNullOrEmpty(s))
          .ToHashSet();


        Console.WriteLine($"[{agregadorId}] Vou subscrever estes sensores: {string.Join(", ", sensores)}");

        // lança a thread do subscriber, passando a lista
        new Thread(() => RabbitSubscriberLoop(sensores)).Start();

        // Inicia o TCPListener
        listener = new TcpListener(IPAddress.Any, porta);
        listener.Start();
        Console.WriteLine($"[{agregadorId}] À escuta na porta {porta}...");

        // Envia REGISTO ao Servidor
        EnviarRegistoAoServidor();

        // Threads auxiliares: 1) comandos do utilizador; 2) envio diário
        new Thread(() => OuvirComandos()).Start();
        new Thread(() => EnviarParaServidorDiariamente()).Start();

        // Loop para aceitar conexões de WAVYs
        try
        {
            while (true)
            {
                TcpClient client = listener.AcceptTcpClient();
                Thread thread = new Thread(() => HandleWavy(client));
                thread.Start();
            }
        }
        catch (SocketException)
        {
            // se paramos o listener => cai aqui
        }
        finally
        {
            RemoverRegistoAgregador();
            Console.WriteLine($"[{agregadorId}] Registo removido. Agregador encerrado.");
        }
    }

    static void RabbitSubscriberLoop(HashSet<string> sensores)
    {
        var factory = new ConnectionFactory { HostName = "localhost" };
        using var conn = factory.CreateConnection();
        using var channel = conn.CreateModel();

        channel.ExchangeDeclare("wavys_data", ExchangeType.Topic, durable: true);
        var queueName = channel.QueueDeclare().QueueName;
        channel.QueueBind(queueName, "wavys_data", "wavy.*.*");

        var consumer = new EventingBasicConsumer(channel);
        consumer.Received += (model, ea) =>
        {
            // 1) Extrai Origem e Formato da routing key
            var rk = ea.RoutingKey.Split('.');
            string origem = rk.Length >= 2 ? rk[1] : "UNKNOWN";
            string formato = rk.Length >= 3 ? rk[2] : "csv";

            // 2) Lê o payload bruto
            string payload = Encoding.UTF8.GetString(ea.Body.Span);
            Console.WriteLine($"[{agregadorId}][RABBIT] ← {origem} ({formato}): {payload}");

            // 3) Empacota RawData para o serviço de pré-processamento
            var raw = new RawData
            {
                Origem = origem,
                Tipo = formato,
                Timestamp = DateTime.UtcNow.ToString("o")
            };
            raw.Payload.Add(ByteString.CopyFromUtf8(payload));

            PreprocessResponse preResp = null;
            try
            {
                preResp = _preprocessClient.Preprocess(raw);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{agregadorId}] ❗ Preprocess RPC falhou: {ex.Message}");
            }

            if (preResp != null)
            {
                // 4a) Usa sempre o resultado do PreprocessingService
                foreach (var sample in preResp.Samples)
                {
                    // só grava se for um sensor subscrito
                    if (sensores.Contains(sample.Tipo))
                        ProcessarDado(
                          sample.Origem,
                          agregadorId,
                          sample.Tipo,
                          sample.Valor,
                          sample.Timestamp
                        );
                }
            }
            else
            {
                // 4b) Fallback local (CSV, JSON, XML) só se o RPC falhar
                if (formato == "csv")
                {
                    var parts = payload.Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length >= 2 && sensores.Contains("Temperatura"))
                        ProcessarDado(origem, agregadorId, "Temperatura",
                                     double.Parse(parts[1].Replace(',', '.')), parts[0]);
                }
                else if (formato == "json")
                {
                    var doc = JsonDocument.Parse(payload).RootElement;
                    string ts = doc.GetProperty("ts").GetString()!;
                    if (sensores.Contains("Temperatura"))
                        ProcessarDado(origem, agregadorId,
                                     "Temperatura", doc.GetProperty("t").GetDouble(), ts);
                    if (sensores.Contains("Velocidade do Vento"))
                        ProcessarDado(origem, agregadorId,
                                     "Velocidade do Vento", doc.GetProperty("v").GetDouble(), ts);
                    if (sensores.Contains("Humidade"))
                        ProcessarDado(origem, agregadorId,
                                     "Humidade", doc.GetProperty("h").GetDouble(), ts);
                }
                else if (formato == "xml")
                {
                    var root = XDocument.Parse(payload).Root;
                    string ts = root.Element("ts")?.Value ?? DateTime.UtcNow.ToString("o");
                    double ParseTag(string tag) =>
                        double.Parse(root.Element(tag)?.Value.Replace(',', '.') ?? "0");

                    if (sensores.Contains("Temperatura"))
                        ProcessarDado(origem, agregadorId,
                                     "Temperatura", ParseTag("t"), ts);
                    if (sensores.Contains("Velocidade do Vento"))
                        ProcessarDado(origem, agregadorId,
                                     "Velocidade do Vento", ParseTag("v"), ts);
                    if (sensores.Contains("Humidade"))
                        ProcessarDado(origem, agregadorId,
                                     "Humidade", ParseTag("h"), ts);
                }
                else
                {
                    Console.WriteLine($"[{agregadorId}] Fallback desconhecido para formato '{formato}'");
                }
            }
        };

        channel.BasicConsume(queueName, autoAck: true, consumer);

        // mantém a thread viva
        while (true) Thread.Sleep(1000);
    }


    static void ProcessarDado(string wavyId, string agrId, string tipo, double valor, string ts)
    {
        string path = CaminhoFicheiro(tipo);
        var m = ObterMutexParaFicheiro(path);
        m.WaitOne();
        try
        {
            File.AppendAllText(path,
                $"DADOS | {wavyId} | {agrId} | {tipo} | {valor:F1} | {ts}\n");
            AtualizarEstadoNoFicheiro(wavyId, "operação", agrId);
        }
        finally { m.ReleaseMutex(); }
    }



    /// Recebe mensagens das WAVYs (DADOS, ESTADO, etc.).
    /// Caso seja DADOS => guarda no ficheiro correspondente.
    static void HandleWavy(TcpClient client)
    {
        NetworkStream stream = client.GetStream();
        byte[] buffer = new byte[4096];
        int bytesRead = stream.Read(buffer, 0, buffer.Length);
        string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
        Console.WriteLine($"[{agregadorId}] Mensagem recebida: {message}");

        string resposta="";

        // Se for um REGISTO, guarda o wavyId
        if (message.StartsWith("REGISTO"))
        {
            var partes = message.Split('|', StringSplitOptions.TrimEntries);
            string wavyId = partes.Length >= 2 ? partes[1] : "UNKNOWN";
            lock (_wavysLock)
                _wavys.Add(wavyId);
            resposta = $"CONFIRMADO | {agregadorId} | RECEBIDO";
        }
        // Se for dados, verifica dulu se o wavyId está registado
        else if (message.StartsWith("DADOS"))
        {
            var linhas = message
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .ToArray();

            // wavyId vem em partes[1]
            var primeiro = linhas[0].Split('|', StringSplitOptions.TrimEntries);
            string wavyId = primeiro.Length >= 2 ? primeiro[1] : null;

            lock (_wavysLock)
            {
                if (wavyId == null || !_wavys.Contains(wavyId))
                {
                    Console.WriteLine($"[{agregadorId}] Ignorando DADOS de '{wavyId}' (não registado)");
                    resposta = $"ERRO | {agregadorId} | WAVY_NAO_REGISTADA";
                    stream.Write(Encoding.UTF8.GetBytes(resposta));
                    client.Close();
                    return;
                }
            }

            // — segue o teu processamento normal de RPC Preprocess ou fallback —
            var raw = new RawData
            {
                Origem = agregadorId,
                Tipo = primeiro[3],
                Timestamp = DateTime.UtcNow.ToString("o")
            };
            foreach (var linha in linhas)
                raw.Payload.Add(ByteString.CopyFromUtf8(linha));

            PreprocessResponse preResp = null;
            try
            {
                preResp = _preprocessClient.Preprocess(raw);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{agregadorId}] Erro RPC Preprocess: {ex.Message}. Usando fallback.");
            }

            if (preResp != null)
            {
                foreach (var sample in preResp.Samples)
                {
                    string path = CaminhoFicheiro(sample.Tipo);
                    var m = ObterMutexParaFicheiro(path);
                    m.WaitOne();
                    try
                    {
                        string linha = $"DADOS | {sample.Origem} | {agregadorId} | {sample.Tipo} | {sample.Valor:F1} | {sample.Timestamp}";
                        File.AppendAllText(path, linha + Environment.NewLine);
                        AtualizarEstadoNoFicheiro(sample.Origem, "operação", agregadorId);
                    }
                    finally { m.ReleaseMutex(); }
                }
            }
            else
            {
                // fallback original…
                foreach (var linha in linhas)
                {
                    var partes = linha.Split('|', StringSplitOptions.TrimEntries);
                    string tipo = partes[3];
                    string path = CaminhoFicheiro(tipo);
                    var m = ObterMutexParaFicheiro(path);
                    m.WaitOne();
                    try
                    {
                        File.AppendAllText(path, linha + Environment.NewLine);
                        AtualizarEstadoNoFicheiro(partes[1], "operação", agregadorId);
                    }
                    finally { m.ReleaseMutex(); }
                }
            }

            resposta = $"CONFIRMADO | {agregadorId} | RECEBIDO";
        }
        else if (message.StartsWith("DESLIGAR"))
        {
            resposta = $"CONFIRMADO | {agregadorId} | RECEBIDO";
        }
        else if (message.StartsWith("ESTADO"))
        {
            // idem ao teu código
            var partes = message.Split('|', StringSplitOptions.TrimEntries);
            if (partes.Length >= 4)
            {
                string wavy = partes[1], novo = partes[3];
                AtualizarEstadoNoFicheiro(wavy, novo, agregadorId);
                resposta = $"CONFIRMADO | {agregadorId} | {wavy}";
            }
            else
            {
                resposta = $"ERRO | {agregadorId} | FORMATO_INCOMPLETO";
            }
        }
        else if (message.StartsWith("COMANDO"))
        {
            // idem…
            // envia, sair, etc.
        }
        else
        {
            resposta = $"ERRO | {agregadorId} | MENSAGEM_DESCONHECIDA";
        }

        // envia a resposta final
        stream.Write(Encoding.UTF8.GetBytes(resposta));
        client.Close();
    }


    /// Envia todos os dados (Temperatura, Pressão, Vento, Humidade) ao Servidor,
    /// agrupando e calculando média/volume. Depois apaga se o Servidor confirmar.

    static void EnviarDadosParaServidor()
    {
        // Lista de tipos que podem existir
        string[] tipos = { "Temperatura", "Pressão", "Humidade", "Velocidade do Vento" };
        // Obtem o caminho de cada ficheiro deste agregador e filtra só os existentes
        string[] ficheiros = tipos
            .Select(tipo => CaminhoFicheiro(tipo))
            .Where(File.Exists)
            .ToArray();

        List<string> todasAsLinhas = new List<string>();

        // 1) Ler cada ficheiro com mutex
        foreach (string ficheiro in ficheiros)
        {
            // Obter/ criar Mutex para este ficheiro
            Mutex fileMutex = ObterMutexParaFicheiro(ficheiro);
            fileMutex.WaitOne();
            try
            {
                // Lê as linhas se existir
                var linhas = File.ReadAllLines(ficheiro);
                if (linhas.Length > 0)
                {
                    todasAsLinhas.AddRange(linhas);
                }
            }
            finally
            {
                fileMutex.ReleaseMutex(); // libertar
            }
        }

        if (todasAsLinhas.Count == 0)
        {
            Console.WriteLine($"[{agregadorId}] Não há dados para enviar.");
            return;
        }

        // 2) Processar e agrupar para calcular volume e média
        var parsed = todasAsLinhas
            .Select(l => l.Split('|', StringSplitOptions.TrimEntries))
            .Where(p => p.Length >= 6 && p[0] == "DADOS")
            .ToList();

        // Agrupa por p[3] => TIPO (Temperatura, Pressão, etc.)
        var agrupados = parsed.GroupBy(p => p[3]);

        // Pacote final: linhas brutas + linhas de média
        List<string> pacoteLinhas = new List<string>();

        // Adicionamos as linhas brutas
        foreach (var parts in parsed)
        {
            pacoteLinhas.Add(string.Join(" | ", parts));
        }

        // Para cada grupo, calculamos média
        foreach (var grupo in agrupados)
        {
            string tipo = grupo.Key;
            // p[4] é VALOR
            var valores = grupo.Select(g => double.Parse(g[4])).ToList();
            double media = valores.Average();
            int volume = valores.Count;
            string ts = DateTime.UtcNow.ToString("o");

            // Exemplo:
            // "DADOS | AGREGADOR_ID | SERVIDOR_01 | tipo | media | volume | media | timestamp"
            string linhaMedia = $"DADOS | {agregadorId} | SERVIDOR_01 | {tipo} | {media:F1} | {volume} | media | {ts}";
            pacoteLinhas.Add(linhaMedia);
        }

        // 3) Montar a string final e enviar ao Servidor
        string pacoteFinal = string.Join("\n", pacoteLinhas);

        string resposta;
        try
        {
            using TcpClient client = new TcpClient("127.0.0.1", 9000);
            NetworkStream stream = client.GetStream();
            byte[] msgBytes = Encoding.UTF8.GetBytes(pacoteFinal);
            stream.Write(msgBytes, 0, msgBytes.Length);

            // Ler resposta
            byte[] buffer = new byte[1024];
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            resposta = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            Console.WriteLine($"[{agregadorId}] Resposta do servidor: {resposta}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{agregadorId}] Erro ao enviar dados ao Servidor: {ex.Message}");
            return;
        }

        // 4) Se confirmou, apagar (esvaziar) os ficheiros com mutex
        if (resposta.StartsWith("CONFIRMADO"))
        {
            foreach (string ficheiro in ficheiros)
            {
                Mutex fileMutex = ObterMutexParaFicheiro(ficheiro);
                fileMutex.WaitOne();
                try
                {
                    // Esvaziar o ficheiro
                    File.WriteAllText(ficheiro, "");
                }
                finally
                {
                    fileMutex.ReleaseMutex();
                }
            }
            Console.WriteLine($"[{agregadorId}] Dados confirmados. Ficheiros de dados esvaziados.");
        }
        else
        {
            Console.WriteLine($"[{agregadorId}] O Servidor não confirmou. Mantemos os ficheiros para reenvio.");
        }
    }


    /// Thread que envia automaticamente à meia-noite.

    static void EnviarParaServidorDiariamente()
    {
        while (true)
        {
            DateTime agora = DateTime.Now;
            DateTime proximo = agora.Date.AddDays(1); // 0h do dia seguinte
            TimeSpan esperar = proximo - agora;
            Thread.Sleep(esperar);
            EnviarDadosParaServidor();
        }
    }

    /// Thread para ler comandos do utilizador (ex.: "enviar", "sair")
    static void OuvirComandos()
    {
        while (true)
        {
            Console.WriteLine($"[{agregadorId}] Escreve 'enviar' para enviar dados ou 'sair' para desligar:");
            string comando = Console.ReadLine();
            if (comando?.ToLower() == "enviar")
            {
                EnviarDadosParaServidor();
            }
            else if (comando?.ToLower() == "sair")
            {
                MarcarWavysAssociadasComoDesligadas();
                listener.Stop();
                RemoverRegistoAgregador();
                EnviarAvisoDesligarAoServidor();
                ApagarFicheirosDoAgregador();
                Environment.Exit(0);

            }
        }
    }

    /// Envia "REGISTO | AGREGADOR_ID | SERVIDOR_01 | timestamp"
    static void EnviarRegistoAoServidor()
    {
        string timestamp = DateTime.UtcNow.ToString("o");
        string msg = $"REGISTO | {agregadorId} | SERVIDOR_01 | {timestamp}";
        try
        {
            using TcpClient client = new TcpClient("127.0.0.1", 9000);
            NetworkStream stream = client.GetStream();
            byte[] data = Encoding.UTF8.GetBytes(msg);
            stream.Write(data, 0, data.Length);

            byte[] buffer = new byte[1024];
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            string resposta = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            Console.WriteLine($"[{agregadorId}] Registo enviado ao servidor: {resposta}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{agregadorId}] Erro ao enviar registo: {ex.Message}");
        }
    }

    /// Remove este Agregador do ficheiro config (agregadores_config.txt)
    static void RemoverRegistoAgregador()
    {
        if (File.Exists(configFile))
        {
            var linhas = File.ReadAllLines(configFile)
                             .Where(l => !l.StartsWith($"{agregadorId}|"))
                             .ToList();
            File.WriteAllLines(configFile, linhas);
        }
    }

    /// Envia "DESLIGAR | AGREGADOR_ID | SERVIDOR_01 | timestamp"
    static void EnviarAvisoDesligarAoServidor()
    {
        string timestamp = DateTime.UtcNow.ToString("o");
        string msg = $"DESLIGAR | {agregadorId} | SERVIDOR_01 | {timestamp}";
        try
        {
            using TcpClient client = new TcpClient("127.0.0.1", 9000);
            NetworkStream stream = client.GetStream();
            byte[] data = Encoding.UTF8.GetBytes(msg);
            stream.Write(data, 0, data.Length);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{agregadorId}] Erro ao enviar aviso de desligar: {ex.Message}");
        }
    }

    /// Atualiza o estado de uma WAVY no ficheiro "estado_wavys.txt".
    /// Linha ex: "WAVY_01:manutencao::2025-04-20T10:20:00Z"
    static void AtualizarEstadoNoFicheiro(string wavyId, string novoEstado, string agregatorId)

    {
        if (!File.Exists(wavysEstadoFile))
            File.WriteAllText(wavysEstadoFile, "");

        var linhas = File.ReadAllLines(wavysEstadoFile).ToList();
        bool found = false;
        for (int i = 0; i < linhas.Count; i++)
        {
            var campos = linhas[i].Split(':');
            if (campos[0] == wavyId)
            {
                campos[1] = novoEstado;
                campos[2] = agregatorId;
                linhas[i] = string.Join(":", campos);
                found = true;
                break;
            }
        }
        if (!found)
        {
            string line = $"{wavyId}:{novoEstado}:{agregadorId}";
            linhas.Add(line);
        }
        File.WriteAllLines(wavysEstadoFile, linhas);
    }


    /// Gera um ID e uma porta livres (AGREGADOR_XX) e basePorta=8000.
    /// Guarda no configFile => "AGREGADOR_01|8000" ...

    static (string, int) GerarAgregadorDisponivel()
    {
        int basePorta = 8000;
        string baseId = "AGREGADOR_";
        int contador = 1;

        if (!File.Exists(configFile))
            File.WriteAllText(configFile, "");

        var linhas = File.ReadAllLines(configFile);
        var portasOcupadas = linhas.Select(l => int.Parse(l.Split('|')[1])).ToHashSet();

        while (portasOcupadas.Contains(basePorta + contador - 1))
            contador++;

        return ($"{baseId}{contador}", basePorta + contador - 1);
    }
    static void ApagarFicheirosDoAgregador()
    {
        string[] tipos = { "Temperatura", "Pressão", "Humidade", "Velocidade do Vento" };
        foreach (string tipo in tipos)
        {
            string path = CaminhoFicheiro(tipo);
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                    Console.WriteLine($"[INFO] Ficheiro apagado: {path}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERRO] Ao apagar ficheiro {path}: {ex.Message}");
                }
            }
        }
    }
    /// Retorna (ou cria) um Mutex associado a um caminho de ficheiro.
    /// Garante exclusão mútua no acesso a esse ficheiro.
    static Mutex ObterMutexParaFicheiro(string filePath)
    {
        lock (ficheiroMutexesLock)
        {
            // Se ainda não existe, cria um Mutex nomeado
            if (!ficheiroMutexes.ContainsKey(filePath))
            {
                // Nome do mutex pode ser algo sem caracteres especiais
                string nomeMutex = "mutex_" + filePath.Replace("\\", "_").Replace(":", "");
                ficheiroMutexes[filePath] = new Mutex(false, nomeMutex);
            }
            return ficheiroMutexes[filePath];
        }
    }

    static void MarcarWavysAssociadasComoDesligadas()
    {
        // Ficheiro com linhas: "WAVY_01:operação:AGREGADOR_01"
        string path = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\estado_wavys.txt";
        if (!File.Exists(path))
            return;

        var linhas = File.ReadAllLines(path).ToList();
        for (int i = 0; i < linhas.Count; i++)
        {
            // parted[0] = "WAVY_01", parted[1] = "operação", parted[2] = "AGREGADOR_01"
            var partes = linhas[i].Split(':');
            if (partes.Length >= 3)
            {
                // Se parted[2] for o ID do agregador
                if (partes[2] == agregadorId)
                {
                    // Marca parted[1] => "Desligada" (pois parted[1] é o estado)
                    partes[1] = "Desligada";

                    // Se houver parted[3] como timestamp (opcional), podes atualizar:
                    // if (partes.Length >= 4)
                    //     partes[3] = DateTime.UtcNow.ToString("o");

                    // Reconstrói a linha
                    linhas[i] = string.Join(":", partes);
                }
            }
        }
        File.WriteAllLines(path, linhas);
    }
}
