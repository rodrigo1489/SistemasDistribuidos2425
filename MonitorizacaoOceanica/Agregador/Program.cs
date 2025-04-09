using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

class Agregador
{
    // Identificador e porta do Agregador
    static string agregadorId;
    static int porta;

    // Ficheiro global de config (ID|PORTA) para saber que Agregadores existem
    static string configFile = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\agregadores_config.txt";

    // Estado das WAVYs - "WAVY_01:operação::2025-04-20T10:00:00Z"
    static string wavysEstadoFile = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\estado_wavys.txt";

    static TcpListener listener;

    // Base de onde os ficheiros são criados
    static string basePath = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\agregador_data\";

    // Retorna o caminho para um tipo de dado com base no agregador atual
    static string CaminhoFicheiro(string tipo)
    {
        return Path.Combine(basePath, $"{tipo}_{agregadorId}.txt");
    }

    static Mutex instanceMutex;



    static void Main()
    {
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

        // Gera ID e porta livres
        (agregadorId, porta) = GerarAgregadorDisponivel();
        Console.WriteLine($"[INFO] Este agregador vai correr como {agregadorId} na porta {porta}.");

        // Adiciona este Agregador no ficheiro global
        File.AppendAllText(configFile, $"{agregadorId}|{porta}\n");

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

    /// Recebe mensagens das WAVYs (DADOS, ESTADO, etc.).
    /// Caso seja DADOS => guarda no ficheiro correspondente.
    static void HandleWavy(TcpClient client)
    {
        NetworkStream stream = client.GetStream();
        byte[] buffer = new byte[4096];
        int bytesRead = stream.Read(buffer, 0, buffer.Length);
        string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

        Console.WriteLine($"[{agregadorId}] Mensagem recebida: {message}");

        string resposta;
        if (message.StartsWith("DADOS"))
        {
            // Podem vir várias linhas separadas por \n
            string[] linhas = message.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (string linha in linhas)
            {
                string[] partes = linha.Split('|', StringSplitOptions.TrimEntries);
                // Ex.: parted[0]=DADOS parted[1]=WAVY_01 parted[2]=AGREGADOR_01 parted[3]=TIPO parted[4]=VALOR parted[5]=TIMESTAMP
                if (partes.Length >= 6)
                {
                    string tipo = partes[3];
                    // Decide qual caminho usar
                    string path = CaminhoFicheiro(tipo);
                    if (!string.IsNullOrEmpty(path))
                    {
                        // Acrescenta esta linha ao ficheiro do tipo
                        File.AppendAllText(path, linha + Environment.NewLine);
                        string wavyId = partes[1];
                        AtualizarEstadoNoFicheiro(wavyId, "operação");
                    }
                    else
                    {
                        Console.WriteLine($"[WARNING] Tipo '{tipo}' não corresponde a nenhum ficheiro fixo.");
                    }
                }
            }
            resposta = $"CONFIRMADO | {agregadorId} | RECEBIDO";
        }
        else if (message.StartsWith("REGISTO"))
        {
            // WAVY a registar => podes atualizar estado
            resposta = $"CONFIRMADO | {agregadorId} | RECEBIDO";
        }
        else if (message.StartsWith("DESLIGAR"))
        {
            // WAVY a desligar
            resposta = $"CONFIRMADO | {agregadorId} | RECEBIDO";
        }
        else if (message.StartsWith("ESTADO"))
        {
            // "ESTADO | WAVY_01 | AGREGADOR_01 | manutencao"
            string[] partes = message.Split('|', StringSplitOptions.TrimEntries);
            if (partes.Length >= 4)
            {
                string wavyId = partes[1];
                string novoEstado = partes[3];
                AtualizarEstadoNoFicheiro(wavyId, novoEstado);
                resposta = $"CONFIRMADO | {agregadorId} | {wavyId}";
            }
            else
            {
                resposta = $"ERRO | {agregadorId} | FORMATO_INCOMPLETO";
            }
        }
        else
        {
            resposta = $"ERRO | {agregadorId} | MENSAGEM_DESCONHECIDA";
        }

        // Envia resposta
        byte[] respBytes = Encoding.UTF8.GetBytes(resposta);
        stream.Write(respBytes, 0, respBytes.Length);
        client.Close();
    }

    /// Envia todos os dados (Temperatura, Pressão, Vento, Humidade) ao Servidor,
    /// agrupando e calculando média/volume. Depois apaga se o Servidor confirmar.

    static void EnviarDadosParaServidor()
    {
        // Coloca todos os caminhos fixos num array
        // Se tivesses mais tipos, adiciona aqui
        string[] tipos = { "Temperatura", "Pressão", "Humidade", "Velocidade do Vento" };
        string[] ficheiros = tipos
            .Select(tipo => CaminhoFicheiro(tipo))
            .Where(File.Exists)
            .ToArray();


        List<string> todasAsLinhas = new List<string>();

        // Lê cada ficheiro, se existir
        foreach (string ficheiro in ficheiros)
        {
            if (File.Exists(ficheiro))
            {
                var linhas = File.ReadAllLines(ficheiro).ToList();
                if (linhas.Count > 0)
                {
                    todasAsLinhas.AddRange(linhas);
                }
            }
        }

        if (todasAsLinhas.Count == 0)
        {
            Console.WriteLine($"[{agregadorId}] Não há dados para enviar.");
            return;
        }

        // Filtra e agrupa para calcular volume e média
        var parsed = todasAsLinhas
            .Select(l => l.Split('|', StringSplitOptions.TrimEntries))
            .Where(p => p.Length >= 6 && p[0] == "DADOS")
            .ToList();

        // Agrupa por p[3] => TIPO
        var agrupados = parsed.GroupBy(p => p[3]);

        // Pacote final = linhas brutas + linhas de média
        List<string> pacoteLinhas = new List<string>();
        // Adicionamos brutas
        foreach (var parts in parsed)
        {
            pacoteLinhas.Add(string.Join(" | ", parts));
        }

        // Para cada grupo, calculamos media
        foreach (var grupo in agrupados)
        {
            string tipo = grupo.Key;
            // p[4] é VALOR
            var valores = grupo.Select(g => double.Parse(g[4])).ToList();
            double media = valores.Average();
            int volume = valores.Count;
            string ts = DateTime.UtcNow.ToString("o");

            // "DADOS | AGREGADOR_01 | SERVIDOR_01 | tipo | media | volume | media | timestamp"
            string linhaMedia = $"DADOS | {agregadorId} | SERVIDOR_01 | {tipo} | {media:F1} | {volume} | media | {ts}";
            pacoteLinhas.Add(linhaMedia);
        }

        string pacoteFinal = string.Join("\n", pacoteLinhas);

        // Enviar ao Servidor
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

        // Se confirmou, apagar os ficheiros
        if (resposta.StartsWith("CONFIRMADO"))
        {
            foreach (string ficheiro in ficheiros)
            {
                if (File.Exists(ficheiro))
                {
                    File.WriteAllText(ficheiro, ""); // esvazia
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

    /// Atualiza o estado de uma WAVY no ficheiro "wavys_agregador.txt".
    /// Linha ex: "WAVY_01:manutencao::2025-04-20T10:20:00Z"
    static void AtualizarEstadoNoFicheiro(string wavyId, string novoEstado, string dataTypes = "")
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
                if (!string.IsNullOrWhiteSpace(dataTypes))
                    campos[2] = dataTypes;
                campos[3] = DateTime.UtcNow.ToString("o");
                linhas[i] = string.Join(":", campos);
                found = true;
                break;
            }
        }
        if (!found)
        {
            string line = $"{wavyId}:{novoEstado}:{dataTypes}:{DateTime.UtcNow:o}";
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

}
