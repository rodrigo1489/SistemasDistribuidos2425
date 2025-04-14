using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;

class WavyPro
{
    // Identificação e tipo desta WAVY
    static string wavyId;
    static string tipoWavy = "WAVY_Pro";

    // Flag para saber se ainda está ativo
    static bool ativo = true;

    // Ficheiro onde guardamos o ID de cada WAVY que é iniciado
    static string configFile = @"C:\\Users\\rodri\\source\\repos\\SistemasDistribuidos2425.2\\MonitorizacaoOceanica\\wavys_config.txt";

    // Dados de ligação ao Agregador (IP, Porta, ID do Agregador)
    static string agregadorIp;
    static int agregadorPorta;
    static string agregadorId;

    // Contador de erros consecutivos ao enviar dados
    // Se chegar a erroEnvioMax, a WAVY desliga-se
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

        // Gera um ID único (WAVY_01, WAVY_02, etc.) e regista-se
        wavyId = GerarWavyIdDisponivel();
        File.AppendAllText(configFile, wavyId + "\n");

        // Caso o programa receba 3 argumentos (ip, porta, agregadorId), usa-os;
        // caso contrário, escolhe manualmente no ficheiro de agregadores
        if (args.Length == 3)
        {
            agregadorIp = args[0];
            agregadorPorta = int.Parse(args[1]);
            agregadorId = args[2];
            Console.WriteLine($"[{wavyId}] Recebidos argumentos -> IP={agregadorIp}, Porta={agregadorPorta}, AgregadorID={agregadorId}");
        }
        else
        {
            // Se não há argumentos, pergunta ao utilizador qual Agregador quer usar
            (agregadorIp, agregadorPorta, agregadorId) = EscolherAgregador();
        }

        // Envia uma mensagem de REGISTO ao Agregador
        RegistarWavy(agregadorIp, agregadorPorta, agregadorId);

        // Envia estado "associada" para o Agregador (ficará no estado_wavys.txt)
        EnviarEstado("associada");

        // Cria uma thread que envia dados periodicamente (Temperatura e Pressão) enquanto 'ativo'
        new Thread(() => EnvioPeriodico(agregadorIp, agregadorPorta)).Start();

        // Loop principal de comandos no terminal
        while (true)
        {
            Console.WriteLine($"[{wavyId}] Comando ('manual', 'desligar' ou 'estado <NOVO_ESTADO>'):");
            string comando = Console.ReadLine();

            if (comando == "manual")
            {
                // Enviar dados manualmente
                EnviarDados(agregadorIp, agregadorPorta);
            }
            else if (comando == "desligar")
            {
                // Desligar manual
                ativo = false;
                EnviarAvisoDesligar();
                RemoverRegisto();
                Environment.Exit(0);
            }
            else if (comando.StartsWith("estado "))
            {
                // Muda estado (operação, manutenção, etc.)
                string novoEstado = comando.Substring(7).Trim();
                EnviarEstado(novoEstado);
            }
        }
    }

    // Gera um ID automático único para esta WAVY, do tipo WAVY_01, WAVY_02, etc.
    // Lê do ficheiro configFile para ver quantos já existem e não repetir.
    static string GerarWavyIdDisponivel()
    {
        int contador = 1;
        if (!File.Exists(configFile)) File.WriteAllText(configFile, "");
        var existentes = File.ReadAllLines(configFile);
        while (existentes.Contains($"WAVY_{contador:D2}")) contador++;
        return $"WAVY_{contador:D2}";
    }

    // Escolhe um Agregador a partir do ficheiro agregadores_config.txt, caso 
    // a WAVY não tenha recebido argumentos na linha de comando.
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
        // Retorna IP=127.0.0.1, Porta=partes[1], AgregadorId=partes[0]
        return ("127.0.0.1", int.Parse(partes[1]), partes[0]);
    }

    // Envia mensagem "REGISTO | wavyId | agregadorId | WAVY_Pro" ao Agregador,
    // para indicar que esta WAVY existe.
    static void RegistarWavy(string ip, int porta, string aggId)
    {
        using TcpClient client = new TcpClient(ip, porta);
        NetworkStream stream = client.GetStream();
        string msg = $"REGISTO | {wavyId} | {aggId} | {tipoWavy}";
        stream.Write(Encoding.UTF8.GetBytes(msg));

        // Lê resposta do Agregador
        byte[] buffer = new byte[1024];
        int bytesRead = stream.Read(buffer, 0, buffer.Length);
        Console.WriteLine($"[{wavyId}] Registo: {Encoding.UTF8.GetString(buffer, 0, bytesRead)}");
    }

    // Thread que, enquanto 'ativo', de minuto em minuto lê o estado no Agregador
    // e se o estado for 'operação' ou 'associada', envia dados (Temperatura e Pressão).
    // Caso seja 'manutenção' ou outro, fica pausado.
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
                // podes considerar 'associada' também envia no arranque
                EnviarDados(ip, porta);
            }
            else
            {
                // ex.: manutenção, desativada, etc.
                Console.WriteLine($"[{wavyId}] Estado atual no agregador é '{estado}' — envio automático pausado.");
            }

            // Espera 60 segundos até o próximo envio
            Thread.Sleep(60000);
        }
    }

    // Envia ambos os tipos de sensor desta WAVY_Pro: Temperatura e Pressão.
    static void EnviarDados(string ip, int porta)
    {
        // WAVY_Pro => Temperatura e Pressão
        Enviar(ip, porta, "Temperatura", 18, 26);
        Enviar(ip, porta, "Pressão", 980, 1050);
    }

    // Função auxiliar para enviar um valor aleatório de um tipo de sensor (Temperatura ou Pressão)
    // e ler a resposta do Agregador. Se der erro consecutivo, ao atingir erroEnvioMax, desliga.

    static void Enviar(string ip, int porta, string tipo, int min, int max)
    {
        Random rand = new Random();
        double valor = rand.Next(min, max) + rand.NextDouble();
        string timestamp = DateTime.UtcNow.ToString("o");
        // Formato: "DADOS | wavyId | agregadorId | tipoDado | valor | timestamp"
        string msg = $"DADOS | {wavyId} | {agregadorId} | {tipo} | {valor:F1} | {timestamp}";

        try
        {
            using TcpClient client = new TcpClient(ip, porta);
            NetworkStream stream = client.GetStream();
            // Envia a mensagem
            stream.Write(Encoding.UTF8.GetBytes(msg));

            // Lê a resposta do Agregador
            byte[] buffer = new byte[1024];
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            Console.WriteLine($"[{wavyId}] {tipo} enviada: {Encoding.UTF8.GetString(buffer, 0, bytesRead)}");

            // Se chegou aqui, reset ao contador de erros (foi bem sucedido)
            erroEnvioCount = 0;
        }
        catch (Exception ex)
        {
            // Falhou o envio (Agregador indisponível?), incrementa o contador de falhas
            erroEnvioCount++;
            Console.WriteLine($"[{wavyId}] Erro ao enviar (contagem {erroEnvioCount}/{erroEnvioMax}): {ex.Message}");

            // Se atingiu o limite de erros consecutivos, desliga
            if (erroEnvioCount >= erroEnvioMax)
            {
                Console.WriteLine($"[{wavyId}] Atingiu {erroEnvioCount} erros de envio. A desligar...");
                EnviarAvisoDesligar();  // Se ainda der tempo de avisar o Agregador (provavelmente vai falhar de novo)
                RemoverRegisto();
                Environment.Exit(0);
            }
        }
    }

    // Envia mensagem de ESTADO ("ESTADO | wavyId | agregadorId | novoEstado") ao Agregador.
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

    // Envia mensagem "DESLIGAR | wavyId | agregadorId | timestamp" ao Agregador, 
    // para dizer que esta WAVY se vai encerrar.
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

    // Remove o ID desta WAVY do ficheiro wavys_config.txt quando desliga.
    static void RemoverRegisto()
    {
        if (!File.Exists(configFile)) return;
        var linhas = File.ReadAllLines(configFile).Where(l => l != wavyId).ToList();
        File.WriteAllLines(configFile, linhas);
    }

    // Lê o estado atual desta WAVY no ficheiro 'estado_wavys.txt' para saber
    // se está "operação", "manutenção", "desativada", etc. 
    // Se não encontrar nada, assume "operação" por defeito.
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
