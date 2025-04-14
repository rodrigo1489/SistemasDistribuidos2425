using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Data.SqlClient;
using ClosedXML.Excel;
using System.Collections.Generic;

class Servidor
{
    // Lock para proteger acesso simultâneo ao ficheiro Excel
    static object fileLock = new object();

    // Caminho para o ficheiro Excel onde guardamos os dados
    static string excelFilePath = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\dados_servidor.xlsx";

    // String de conexão à base de dados SQL Server
    static string connString = "Server=localhost,1433;Database=MonitorizacaoOceanica;Trusted_Connection=True;";

    // Dicionário de Mutexes, um por cada Agregador (ou origem)
    // para garantir acesso exclusivo quando várias threads gravam simultaneamente
    static Dictionary<string, Mutex> ficheiroMutexes = new Dictionary<string, Mutex>();

    // Lock para proteger o dicionário de Mutexes acima
    static object mutexLock = new object();

    static void Main()
    {
        // Se o servidor for interrompido (Ctrl+C), limpa ficheiros e sai
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("[SERVIDOR] Encerramento forçado detetado. A limpar ficheiros...");
            LimparFicheirosDeRegisto();
            Environment.Exit(0);
        };

        // Abre um TcpListener na porta 9000, onde aguarda ligações (Agregadores)
        TcpListener listener = new TcpListener(IPAddress.Any, 9000);
        listener.Start();
        Console.WriteLine("[SERVIDOR] À escuta na porta 9000...");

        // Cria uma thread adicional para ler comandos da consola
        // Se digitar "desligar servidor", limpa e sai
        new Thread(() =>
        {
            while (true)
            {
                string? comando = Console.ReadLine();
                if (comando?.ToLower() == "desligar servidor")
                {
                    Console.WriteLine("[SERVIDOR] Comando 'desligar servidor' executado. A encerrar...");
                    LimparFicheirosDeRegisto();
                    ApagarConteudoAgregadorData();
                    Environment.Exit(0);
                }
            }
        }).Start();

        // Loop principal: a cada nova ligação, inicia uma thread para HandleClient
        while (true)
        {
            TcpClient client = listener.AcceptTcpClient();
            Thread thread = new Thread(() => HandleClient(client));
            thread.Start();
        }
    }

    /// Retorna (ou cria) um Mutex específico para cada AgregadorId,
    /// garantindo que só uma thread pode processar dados daquele agregador ao mesmo tempo.
    static Mutex ObterMutexParaAgregador(string agregadorId)
    {
        lock (mutexLock)
        {
            if (!ficheiroMutexes.ContainsKey(agregadorId))
            {
                ficheiroMutexes[agregadorId] = new Mutex();
            }
            return ficheiroMutexes[agregadorId];
        }
    }

    /// Guarda um registo na base de dados SQL Server, na tabela "Registos".
    /// Each registo tem (TipoMensagem,AgregadorId,WavyId,TipoDado,Valor,Volume,Metodo,Timestamp,Origem,Destino).
    static void GuardarNoSQLServer(
        string tipoMensagem,
        string agregadorId,
        string wavyId,
        string tipoDado,
        double valor,
        int volume,
        string metodo,
        string timestampStr,
        string origem,
        string destino
    )
    {
        using var conn = new SqlConnection(connString);
        conn.Open();

        string sql = @"
INSERT INTO Registos
(TipoMensagem, AgregadorId, WavyId, TipoDado, Valor, Volume, Metodo, Timestamp, Origem, Destino)
VALUES (@TipoMensagem, @AgregadorId, @WavyId, @TipoDado, @Valor, @Volume, @Metodo, @Timestamp, @Origem, @Destino);
";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@TipoMensagem", tipoMensagem);
        cmd.Parameters.AddWithValue("@AgregadorId", agregadorId);
        cmd.Parameters.AddWithValue("@WavyId", wavyId);
        cmd.Parameters.AddWithValue("@TipoDado", tipoDado);
        cmd.Parameters.AddWithValue("@Valor", valor);
        cmd.Parameters.AddWithValue("@Volume", volume);
        cmd.Parameters.AddWithValue("@Metodo", metodo);
        cmd.Parameters.AddWithValue("@Timestamp", DateTime.Parse(timestampStr));
        cmd.Parameters.AddWithValue("@Origem", origem);
        cmd.Parameters.AddWithValue("@Destino", destino);

        cmd.ExecuteNonQuery();
    }

    /// Guarda também os dados no Excel "dados_servidor.xlsx", numa folha "Registos".
    /// Cria cabeçalhos se ainda estiver vazia.
    static void GuardarNoExcel(
         string tipoMensagem,
         string agregadorId,
         string wavyId,
         string tipoDado,
         double valor,
         int volume,
         string metodo,
         string timestampStr,
         string origem,
         string destino
     )
    {
        lock (fileLock)
        {
            // Carrega ou cria a workbook
            var wb = File.Exists(excelFilePath) ?
                     new XLWorkbook(excelFilePath) : new XLWorkbook();

            // Usa a folha "Registos"
            string folha = "Registos";
            var ws = wb.Worksheets.Contains(folha) ?
                     wb.Worksheet(folha) :
                     wb.Worksheets.Add(folha);

            // Se não houver linha usada, cria cabeçalhos
            if (ws.LastRowUsed() == null)
            {
                string[] headers = {
                    "TipoMensagem","AgregadorId","WavyId","TipoDado","Valor","Volume","Metodo","Timestamp","Origem","Destino"
                };
                for (int i = 0; i < headers.Length; i++)
                {
                    ws.Cell(1, i + 1).Value = headers[i];
                    ws.Cell(1, i + 1).Style.Font.Bold = true;
                }
            }

            // Última linha usada, +1 para inserir novo registo
            int row = ws.LastRowUsed()?.RowNumber() + 1 ?? 2;

            // Preenche cada coluna
            ws.Cell(row, 1).Value = tipoMensagem;
            ws.Cell(row, 2).Value = agregadorId;
            ws.Cell(row, 3).Value = wavyId;
            ws.Cell(row, 4).Value = tipoDado;
            ws.Cell(row, 5).Value = valor;
            ws.Cell(row, 6).Value = volume;
            ws.Cell(row, 7).Value = metodo;
            ws.Cell(row, 8).Value = timestampStr;
            ws.Cell(row, 9).Value = origem;
            ws.Cell(row, 10).Value = destino;

            // Guarda as alterações
            wb.SaveAs(excelFilePath);
        }
    }

    /// Trata cada cliente (Agregador ou WAVY) que se liga ao servidor.
    /// Lê a mensagem e responde adequadamente (REGISTO, DADOS, DESLIGAR, COMANDO...).
    static void HandleClient(TcpClient client)
    {
        var stream = client.GetStream();
        byte[] buffer = new byte[4096];
        int bytesRead = stream.Read(buffer, 0, buffer.Length);
        string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
        Console.WriteLine("[SERVIDOR] Recebido: \n" + message);

        // Inicializa a resposta (tem de ter sempre algum valor, evitando "use of unassigned local variable")
        string resposta = "";

        try
        {
            // REGISTO ou DESLIGAR => apenas confirma
            if (message.StartsWith("REGISTO") || message.StartsWith("DESLIGAR"))
            {
                resposta = "CONFIRMADO | SERVIDOR_01 | RECEBIDO";
            }
            else if (message.StartsWith("DADOS"))
            {
                // Pode vir várias linhas (separadas por \n) se o Agregador agrupar
                string[] linhas = message.Split("\n", StringSplitOptions.RemoveEmptyEntries);

                // Descobre o agregadorId para o Mutex
                string agregadorIdParaMutex = "DESCONHECIDO";
                if (linhas.Length > 0)
                {
                    var partesTemp = linhas[0].Split('|', StringSplitOptions.TrimEntries);
                    if (partesTemp.Length >= 2)
                    {
                        // parted[1] costuma ser "AGREGADOR_XX" ou "WAVY_XX"
                        agregadorIdParaMutex = partesTemp[1];
                    }
                }

                // Obter/ criar Mutex para este agregador
                Mutex mutex = ObterMutexParaAgregador(agregadorIdParaMutex);
                mutex.WaitOne(); // bloqueia até ter acesso exclusivo

                try
                {
                    // Para cada linha "DADOS..."
                    foreach (string linha in linhas)
                    {
                        string[] partes = linha.Split("|", StringSplitOptions.TrimEntries);

                        if (partes.Length >= 5)
                        {
                            // parted[0]=DADOS parted[1]=Origem parted[2]=Destino parted[3]=tipoDado parted[4]=valor
                            string tipoMensagem = partes[0];
                            string origem = partes[1];
                            string destino = partes[2];
                            string tipoDado = partes[3];
                            double valor = double.Parse(partes[4]);

                            // Podem existir volume, metodo, e timestamp no final
                            int volume = 0;
                            string metodo = "";
                            string timestampStr = DateTime.UtcNow.ToString("o");

                            if (partes.Length >= 6)
                            {
                                // parted[5] pode ser volume ou timestamp
                                bool isInt = int.TryParse(partes[5], out volume);
                                if (!isInt)
                                {
                                    // Então parted[5] é timestamp
                                    volume = 0;
                                    timestampStr = partes[5];
                                }

                                if (partes.Length >= 7)
                                {
                                    // parted[6] pode ser "media", "bruto" ou outro
                                    if (partes[6] == "media" || partes[6] == "bruto")
                                    {
                                        metodo = partes[6];
                                        // parted[7] (se existir) é timestamp
                                        if (partes.Length >= 8)
                                        {
                                            timestampStr = partes[7];
                                        }
                                    }
                                    else
                                    {
                                        // parted[6] é timestamp
                                        timestampStr = partes[6];
                                    }
                                }
                            }

                            // Decide se a origem é AGREGADOR_XX ou WAVY_XX
                            string wavyId = "";
                            string agregadorId = "";
                            if (origem.StartsWith("AGREGADOR_"))
                                agregadorId = origem;
                            else if (origem.StartsWith("WAVY_"))
                                wavyId = origem;

                            // Guarda no SQL e no Excel
                            GuardarNoSQLServer(tipoMensagem, agregadorId, wavyId, tipoDado, valor, volume, metodo, timestampStr, origem, destino);
                            GuardarNoExcel(tipoMensagem, agregadorId, wavyId, tipoDado, valor, volume, metodo, timestampStr, origem, destino);
                        }
                        else
                        {
                            Console.WriteLine("[SERVIDOR] Mensagem DADOS sem campos suficientes: " + linha);
                        }
                    }
                }
                finally
                {
                    // Liberta o Mutex mesmo que haja erro
                    mutex.ReleaseMutex();
                }

                resposta = "CONFIRMADO | SERVIDOR_01 | RECEBIDO";
            }
            else if (message.StartsWith("COMANDO"))
            {
                // Se quisermos comandos tipo "desligar_servidor" via TCP
                string[] partes = message.Split('|', StringSplitOptions.TrimEntries);
                if (partes.Length >= 4)
                {
                    string acao = partes[3];
                    if (acao == "desligar_servidor")
                    {
                        // Fecha o servidor
                        resposta = "CONFIRMADO | SERVIDOR_01 | DESLIGAR_SERVER_OK";
                        // Responde antes de encerrar
                        stream.Write(Encoding.UTF8.GetBytes(resposta));

                        LimparFicheirosDeRegisto();
                        ApagarConteudoAgregadorData();
                        Environment.Exit(0);
                    }
                    else
                    {
                        resposta = $"ERRO | SERVIDOR_01 | COMANDO DESCONHECIDO: {acao}";
                    }
                }
            }
            else
            {
                resposta = "ERRO | SERVIDOR_01 | MENSAGEM DESCONHECIDA";
            }
        }
        catch (Exception ex)
        {
            // Se der erro, responde com
            resposta = $"ERRO | SERVIDOR_01 | {ex.Message}";
        }

        // Por fim, envia a resposta ao client
        stream.Write(Encoding.UTF8.GetBytes(resposta));
        client.Close();
    }

    /// Limpa o conteúdo de ficheiros que guardam a config de agregadores, WAVYs e estados,
    /// geralmente usado ao encerrar o servidor.
    static void LimparFicheirosDeRegisto()
    {
        string[] ficheiros = {
            @"C:\\Users\\rodri\\source\\repos\\SistemasDistribuidos2425.2\\MonitorizacaoOceanica\\agregadores_config.txt",
            @"C:\\Users\\rodri\\source\\repos\\SistemasDistribuidos2425.2\\MonitorizacaoOceanica\\wavys_config.txt",
            @"C:\\Users\\rodri\\source\\repos\\SistemasDistribuidos2425.2\\MonitorizacaoOceanica\\estado_wavys.txt",
        };

        foreach (string ficheiro in ficheiros)
        {
            if (File.Exists(ficheiro))
            {
                File.WriteAllText(ficheiro, "");
                Console.WriteLine($"[SERVIDOR] Ficheiro apagado: {ficheiro}");
            }
        }
    }

    /// Apaga todo o conteúdo da pasta 'agregador_data', onde cada Agregador
    /// mantém ficheiros por tipo (Temperatura.txt, etc.).
    static void ApagarConteudoAgregadorData()
    {
        string pasta = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\agregador_data\";

        if (!Directory.Exists(pasta))
        {
            Console.WriteLine("[SERVIDOR] Pasta de dados de agregadores não existe.");
            return;
        }

        try
        {
            string[] ficheiros = Directory.GetFiles(pasta);
            foreach (string ficheiro in ficheiros)
            {
                File.Delete(ficheiro);
                Console.WriteLine($"[SERVIDOR] Ficheiro apagado: {ficheiro}");
            }

            Console.WriteLine("[SERVIDOR] Todos os ficheiros da pasta agregador_data foram apagados.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SERVIDOR] Erro ao apagar ficheiros: {ex.Message}");
        }
    }
}
