# Monitorização Oceânica

Este repositório contém o **Projeto de Monitorização Oceânica**, um sistema completo para recolha, pré-processamento, análise e visualização de dados oceanográficos de sensores.

## Visão Geral

O sistema é composto por vários componentes:

1. **WAVYs** (coletores de dados):
   - *WAVY_Básica* (temperatura, CSV)
   - *WAVY_Pro* (temperatura + pressão, JSON)
   - *WAVY_Meteorológica* (temperatura, vento, humidade, XML)
   - Publicam dados em RabbitMQ e, em fallback, por TCP.

2. **Agregadores**:
   - Subscrevem tópicos RabbitMQ para sensores selecionados.
   - Enviam `RawData` ao _PreprocessingService_ via gRPC.
   - Guardam _ProcessedSamples_ em ficheiros por sensor.
   - Agrupam e enviam dados ao _Servidor_ (TCP) diariamente ou sob comando.

3. **PreprocessingService (gRPC)**:
   - Converte CSV/JSON/XML em mensagens protobuf `ProcessedSample`.

4. **AnalysisService (gRPC)**:
   - Recebe `ProcessedSample` e calcula média e desvio padrão.

5. **Servidor Central**:
   - Recebe dados agregados (TCP) dos agregadores.
   - Persiste registos e resultados de análise no SQL Server (`Registos`) e num ficheiro Excel.
   - Expõe API REST (Minimal API) para consulta e análise manual:
     - `GET /registos`
     - `GET /analises?sensor=&di=&df=`  
     - `POST /analise/manual`

6. **Interface Web (UI)**:
   - Aplicação single-page com Bootstrap:
     - Visualiza últimos registos.
     - Lista análises automáticas.
     - Dispara análises manuais.

7. **Gestor Principal**:
   - Console que inicia o Servidor, N agregadores e M WAVYs de cada tipo.
   - Permite enviar comandos (`enviar`, `sair`) a cada agregador e ao servidor.

## Requisitos

- .NET 8 SDK
- RabbitMQ ativo em `localhost`
- SQL Server em `localhost,1433` com base `MonitorizacaoOceanica`
- Directórios de configuração e dados com permissões de leitura/escrita

## Como Executar

1. **Clonar o repositório**:
   \`\`\`bash
   git clone https://github.com/SEU_USUARIO/monitorizacao-oceanica.git
   cd monitorizacao-oceanica
   \`\`\`

2. **Configurar a base de dados**:
   - Criar o banco e a tabela \`Registos\` (veja \`scripts/create_tables.sql\`).

3. **Iniciar o Gestor**:
   \`\`\`bash
   cd GestorPrincipal/bin/Debug/net8.0
   ./GestorPrincipal.exe
   \`\`\`
   - Siga prompts para iniciar agregadores e WAVYs.

4. **Aceder à UI**:
   - Abra \`http://localhost:5003\` no navegador.

5. **Interagir**:
   - Navegue entre abas para ver registos, análises automáticas e disparar análises manuais.

## Estrutura de Pastas

\`\`\`
/Servidor20             # Projeto do Servidor (API + gRPC)
/PreprocessingService   # Serviço gRPC de pré-processamento
/AnalysisService        # Serviço gRPC de análise estatística
/Agregador              # Agregador RabbitMQ + TCP
/Wavy.Basica            # Dispositivo WAVY Básica
/Wavy.Pro               # Dispositivo WAVY Pro
/Wavy.Meteorologica     # Dispositivo WAVY Meteorológica
/GestorPrincipal        # Aplicação console para orquestração
/UI                     # Interface Web (HTML/CSS/JS + Program.cs)
/scripts                # Scripts SQL para criar base e tabela
\`\`\`

## Funcionalidades Futuras

- Dashboard gráfico (Chart.js ou Recharts)
- Autenticação e autorização (JWT)
- Métricas e logging centralizado (Serilog + Prometheus)
- Deploy em Docker/Kubernetes

---

📄 **Licença**: MIT
