# Monitorização Oceânica

Este repositório contém um sistema distribuído de monitorização ambiental marinha, composto por vários componentes que comunicam entre si para coletar, processar, armazenar e visualizar dados de sensores (WAVYs) em tempo real. A seguir está a descrição completa de cada parte, requisitos, instruções de instalação e uso.

---

## Sumário

1. [Visão Geral](#visão-geral)  
2. [Componentes](#componentes)  
3. [Pré-requisitos](#pré-requisitos)  
4. [Estrutura do Repositório](#estrutura-do-repositório)  
5. [Configuração Inicial](#configuração-inicial)  
6. [Como Construir e Executar](#como-construir-e-executar)  
   1. [1) AnalysisService (Python)](#1-analysisservice-python)  
   2. [2) PreprocessService (C# gRPC)](#2-preprocessservice-c-grpc)  
   3. [3) Web UI / API (ASP.NET)](#3-web-ui--api-aspnet)  
   4. [4) Servidor Principal (C# TCP)](#4-servidor-principal-c-tcp)  
   5. [5) Gestor (C# CLI)](#5-gestor-c-cli)  
   6. [6) Agregadores (C#)](#6-agregadores-c)  
   7. [7) WAVYs (C#)](#7-wavys-c)  
7. [Banco de Dados SQL Server](#banco-de-dados-sql-server)  
8. [Arquivos de Configuração](#arquivos-de-configuração)  
9. [Comandos do Gestor](#comandos-do-gestor)  
10. [Logs e Diretórios de Saída](#logs-e-diretórios-de-saída)  
11. [Fluxo de Dados](#fluxo-de-dados)  
12. [Contato / Créditos](#contato--créditos)  

---

## Visão Geral

O sistema completo permite que dispositivos sensores (WAVYs) coletem dados de temperatura, pressão, velocidade do vento e umidade em alto mar e enviem para agregadores que:
1. Pré-processam (via gRPC) ou fazem fallback local.
2. Gravem ficheiros por tipo de sensor, com exclusão mútua (mutex).
3. A cada intervalo (diário ou manual), agrupam e calculam média/volume, enviam ao servidor principal via TCP com prefixo de comprimento.
4. O servidor principal persiste em banco de dados (EF Core + SQL Server), encaminha amostras para um serviço de análise (Python gRPC) que retorna média, desvio padrão e outliers.  

Um **Gestor** central inicia e monitora todos os componentes, permite comandos de status, listar agregadores/WAVYs, enviar/desligar/reiniciar agregadores, alterar estado das WAVYs e encerrar tudo.

---

## Componentes

1. **WAVYs (C# .NET 8)**  
   - Três variantes:  
     - `WAVY_Básica` (somente Temperatura, modo CSV via RabbitMQ)  
     - `WAVY_Pro` (Temperatura + Pressão, modo JSON via RabbitMQ)  
     - `WAVY_Meteorológica` (Temperatura, Velocidade do Vento e Umidade, modo XML via RabbitMQ)  
   - Cada WAVY:  
     - Gera valores aleatórios periódicos (60 s)  
     - Publica no exchange `wavys_data` (Topic)  
     - Registra-se junto a um Agregador via TCP (`REGISTO | <WAVY_ID> | <AG_ID> | <TipoWAVY>`) com prefixo de 4 bytes  
     - Envia mensagens de estado e desligamento ao Agregador para pausar/desligar autonomamente.  

2. **Agregador (C# .NET 8)**  
   - Recebe e mantém registro das WAVYs (arquivo `estado_wavys.txt`)  
   - Subscrição RabbitMQ:  
     - BindingKey: `wavy.<AG_ID>.*.*` (só recebe dados das WAVYs dele)  
   - Pré-processamento:  
     - Chama `PreprocessService` (gRPC em :5001)  
     - Fallback local para CSV/JSON/XML  
   - Guarda em ficheiros locais (ex.: `Temperatura_AGREGADOR_1.txt`) com mutex por arquivo  
   - Comandos por TCP:  
     - `REGISTO` / `DADOS` / `ESTADO` / `COMANDO | … | enviar` / `COMANDO | … | sair`  
   - Agrega dados periodicamente (diário ou manual) e envia ao Servidor via TCP (prefixo de comprimento)  
   - Atualiza `agregadores_config.txt` com `AG_ID|porta|sensores…`  

3. **PreprocessService (C# gRPC)**  
   - Porta `5001`  
   - Recebe `RawData { Origem, Tipo, Timestamp, [][]Payload }`  
   - Retorna `PreprocessResponse { repeated ProcessedSample }`  
   - Converte cada Payload CSV/JSON/XML em objeto `ProcessedSample { Origem, Tipo, Valor, Timestamp }`  

4. **AnalysisService (Python gRPC)**  
   - Porta `5002`  
   - RPC: `Analyze(stream ProcessedSample) returns AnalysisResult`  
     - `AnalysisResult { double media, double desviopadrao, map<string,double> outliers }`  
   - Calcula média, desvio padrão e flags outliers (|valor – média| > 2σ)  

5. **Servidor Principal (C# .NET 8)**  
   - Porta `9000` (TCP com prefixo de comprimento)  
   - Recebe:  
     - `REGISTO` / `DESLIGAR` / `DADOS` (raw + médias) / `COMANDO | … | desligar_servidor`  
   - Persistência em **SQL Server**:  
     - `MonitoracaoContext` (EF Core) → tabela `Registos`  
     - Conexão configurável via `appsettings.json` ou string em `Program.cs`  
   - Encaminha raw samples (sem médias) para o `AnalysisService`  
   - Grava análise (média, desvio, volume) no DB  
   - **Não ocorre mais gravação em Excel**  

6. **Web UI / API (ASP.NET Core)**  
   - Porta `5003`  
   - Permite endpoints REST para consulta de dados (futuro dashboard)  
   - Possui Health-check básico (`GET /`)  

7. **Gestor (C# .NET 8 CLI)**  
   - Lança todos os serviços auxiliares (`AnalysisService`, `PreprocessService`, `WebUI/API`, `Servidor`)  
   - Pergunta quantos agregadores iniciar, aguarda arquivo `agregadores_config.txt` preenchido, lê cada linha e pergunta quantas WAVYs de cada tipo para cada agregador, lança executáveis de WAVY com argumentos `127.0.0.1 porta AG_ID`  
   - Oferece prompt interativo com comandos:  
     - `status`  
     - `listar agregadores`  
     - `listar wavys`  
     - `enviar <AG_ID>`, `enviar todos`  
     - `desligar <AG_ID>`, `desligar todos`  
     - `restart <AG_ID>`  
     - `estado <WAVY_ID> <novoEstado>`, `estado todas <novoEstado>`  
     - `exit` (encerra tudo; envia `COMANDO … sair` p/ agregadores, `COMANDO … desligar_servidor` p/ Servidor, mata processos)  

---

## Pré-requisitos

- .NET 8 SDK instalada  
- Python 3.9+  
- RabbitMQ instalado e em execução (localhost:5672)  
- SQL Server ou SQL Express instalado (TCP/IP habilitado)  
- Visual Studio 2022 / VS Code ou equivalente  

---

## Estrutura do Repositório

```
/MonitorizacaoOceanica
├─ /WavyBasica
│   └─ Program.cs
├─ /WavyPro
│   └─ Program.cs
├─ /WavyMeteorologica
│   └─ Program.cs
├─ /Agregador
│   ├─ Program.cs
│   └─ Preprocess (referência gRPC)
├─ /PreprocessService
│   ├─ preprocess.proto
│   └─ Program.cs
├─ /AnalysisService
│   ├─ analyze_pb2.py
│   ├─ analyze_pb2_grpc.py
│   └─ server.py
├─ /Servidor
│   ├─ Program.cs
│   ├─ appsettings.json
│   ├─ Data
│   │    └─ MonitoracaoContext.cs
│   └─ Models
│        └─ Registo.cs
├─ /WebUI
│   └─ Startup.cs
├─ /Gestor
│   └─ Program.cs
├─ agregadores_config.txt   (gerado em tempo de execução)
├─ estado_wavys.txt         (gerado em tempo de execução)
└─ (outros arquivos gerados)
```

---

## Banco de Dados SQL Server

### Criação e atualização da base de dados

1. **String de conexão**  
   Em `Servidor/appsettings.json` (ou diretamente em `Program.cs`), ajuste a string:

   ```json
   "ConnectionStrings": {
     "MonitoracaoDB": "Server=localhost,1433;Database=MonitorizacaoOceanica;Trusted_Connection=True;Encrypt=False;TrustServerCertificate=True"
   }
   ```

2. **EF Core Migrations**  
   No diretório `Servidor`, execute:

   ```bash
   dotnet tool install --global dotnet-ef   # se ainda não tiver o CLI do EF
   dotnet ef migrations add InitialCreate   # gera migração inicial
   dotnet ef database update                # aplica migração e cria a base
   ```

   - Isso criará a tabela `Registos` conforme o modelo de `MonitoracaoContext`.
   - Se precisar atualizar o esquema, modifique os modelos em `Servidor/Models` e então:

     ```bash
     dotnet ef migrations add NomeDaMigracao
     dotnet ef database update
     ```

3. **Verificar no SQL Server**  
   - Abra o SQL Server Management Studio (SSMS) ou Azure Data Studio.
   - Conecte-se a `localhost,1433` e confira se o banco `MonitorizacaoOceanica` foi criado e contém a tabela `Registos`.

---

## Como Construir e Executar

### 1) **AnalysisService (Python)**

```bash
cd AnalysisService
python server.py
```
- Escuta em `0.0.0.0:5002` (gRPC).  

### 2) **PreprocessService (C# gRPC)**

```bash
cd PreprocessService
dotnet run
```
- gRPC em `localhost:5001`.  

### 3) **Web UI / API (ASP.NET Core)**

```bash
cd WebUI
dotnet run --urls http://localhost:5003
```
- Endpoints HTTP em `http://localhost:5003/`.  

### 4) **Servidor Principal (C# TCP + EF Core)**

```bash
cd Servidor
dotnet ef database update   # cria/atualiza a base antes de rodar
dotnet run
```
- TCP: `0.0.0.0:9000`.  
- Persiste apenas em SQL Server, não mais Excel.  

### 5) **Gestor (C# CLI)**

```bash
cd Gestor
dotnet run
```
- Pergunta nº de agregadores e quantas WAVYs iniciar.  
- Inicia Agregadores e WAVYs.  
- Fornece CLI para comandos (status, listar, enviar, desligar, etc.).  

### 6) **Agregadores (C# .NET 8)**

```bash
cd Agregador
dotnet run
```
- Regista em `agregadores_config.txt`.  
- Pergunta sensores; atualiza `agregadores_config.txt` com `ID|porta|sensores`.  
- Consome RabbitMQ, pré-processa, grava em arquivos por sensor, atualiza `estado_wavys.txt`.  
- Envia dados ao Servidor via TCP prefixado.  
- Aceita comandos TCP (`COMANDO enviar/sair`, `ESTADO ...`).  

### 7) **WAVYs (C# .NET 8)**

#### WAVY_Básica  
```bash
cd WavyBasica
dotnet run 127.0.0.1 <porta_AG> <AG_ID>
```

#### WAVY_Pro  
```bash
cd WavyPro
dotnet run 127.0.0.1 <porta_AG> <AG_ID>
```

#### WAVY_Meteorologica  
```bash
cd WavyMeteorologica
dotnet run 127.0.0.1 <porta_AG> <AG_ID>
```

---

## Arquivos de Configuração

- **agregadores_config.txt**  
  Formato: `<AG_ID>|<porta>|<sensoresCSV>`  
  Exemplo: `AGREGADOR_1|8000|Temperatura,Pressão`  

- **estado_wavys.txt**  
  Formato: `<WAVY_ID>:<estado>:<AG_ID>`  
  Exemplo: `WAVY_01:operação:AGREGADOR_1`  

- **agregador_data\<Sensor>_<AG_ID>.txt**  
  Linhas: `DADOS | <WAVY_ID> | <AG_ID> | <Sensor> | <valor> | <timestamp>`  

---

## Comandos do Gestor

| Comando                          | Descrição                                                                                     |
|----------------------------------|------------------------------------------------------------------------------------------------|
| `status`                         | Exibe status dos serviços (5001/5002/5003/9000), agregadores + porta + sensores, WAVYs + estado. |
| `listar agregadores`             | Lista agregadores (`ID | porta | sensores`).                                                    |
| `listar wavys`                   | Lista WAVYs (`WAVY_ID : estado : AG_ID`).                                                       |
| `enviar <AG_ID>`                 | Envia `COMANDO | GESTOR | <AG_ID> | enviar` ao agregador específico.                            |
| `enviar todos`                   | Envia `COMANDO | GESTOR | <AG_ID> | enviar` para todos os agregadores.                          |
| `desligar <AG_ID>`               | Envia `COMANDO | GESTOR | <AG_ID> | sair`; agregador encerra-se.                                 |
| `desligar todos`                 | Envia `COMANDO | GESTOR | <AG_ID> | sair` para todos.                                           |
| `estado <WAVY_ID> <novoEstado>`  | Envia `ESTADO | <WAVY_ID> | <AG_ID> | <novoEstado>`.                                            |
| `estado todas <novoEstado>`      | Envia `ESTADO | WAVY_ID | AG_ID | <novoEstado>` para cada WAVY listada em `estado_wavys.txt`.   |
Agregadores e WAVYs. |

---

## Logs e Diretórios de Saída

- **Logs** (console)  
  - WAVYs: publicações RabbitMQ, registro, estado.  
  - Agregadores: mensagens recebidas, pré-processamento, gravações, envio ao Servidor, resposta a comandos.  
  - Servidor: recebimento, persistência, análise.  
  - Gestor: status, envio de comandos, erros de comunicação.

- **Diretórios**  
  ```
  MonitorizacaoOceanica/
  ├ agregadores_config.txt
  ├ estado_wavys.txt
  ├ agregador_data/
  │   ├ Temperatura_AGREGADOR_1.txt
  │   ├ Pressão_AGREGADOR_1.txt
  │   └ …
  ├ dados_servidor.db (SQL Server LocalDB ou outro)
  └ (outros arquivos gerados)
  ```

---

## Fluxo de Dados

1. **WAVY → RabbitMQ** (csv/json/xml).  
2. **Agregador** consome RabbitMQ → chama **PreprocessService** ou faz fallback → grava em ficheiros por sensor → atualiza `estado_wavys.txt`.  
3. **Agregador** agrega (diário ou `COMANDO enviar`) → envia pacote prefixado ao **Servidor**.  
4. **Servidor** grava raw no **SQL Server** → encaminha raw a **AnalysisService** → recebe média/σ/outliers → grava análise no banco.  
5. **Gestor** interage via TCP com Agregadores/Servidor para status/comandos.

---

## Contato / Créditos

- **Autor:** Rodrigo Fernandes, Alexandre Ferreira, Manuel Almeida

Este projeto foi desenvolvido para fins académicos/profissionais em Sistemas Distribuídos. Reporte issues ou sugerir melhorias no repositório.
