using Microsoft.AspNetCore.Mvc;
using RegistrAi.Api.Data;
using RegistrAi.Api.Models;
using System.Text.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace RegistrAi.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class TransacoesIaController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly string _apiKey;
        private readonly HttpClient _httpClient;
        private static readonly System.Globalization.CultureInfo _culturaBr = new System.Globalization.CultureInfo("pt-BR");

        public TransacoesIaController(AppDbContext context, IConfiguration configuration, IHttpClientFactory httpClientFactory)
        {
            _context = context;
            _apiKey = configuration["OpenAI:ApiKey"]!;
            _httpClient = httpClientFactory.CreateClient();
        }

        private string DispositivoIdAtual => Request.Headers["X-Device-Id"].ToString();

        public class RequisicaoChat
        {
            public string Texto { get; set; } = string.Empty;

             public List<MensagemHistorico> Historico { get; set; } = new();
            // Histórico da conversa — Flutter guarda e manda a cada mensagem
            // Ex: [{ role: "user", content: "gastei 50" }, { role: "assistant", content: "Qual a categoria?" }]

            public int Tentativas { get; set; } = 0;
            // Conta quantas vezes a IA já pediu informação faltando
            // Flutter incrementa a cada pergunta da IA
            // Quando chega em 2 → API retorna mensagem de erro
        }

        public class MensagemHistorico
        {
            public string Role { get; set; } = string.Empty;
            // "user" = mensagem do usuário
            // "assistant" = mensagem da IA

            public string Content { get; set; } = string.Empty;
            // O texto da mensagem
        }

        // O que a API devolve para o Flutter
        public class RespostaChat
        {
            public string Tipo { get; set; } = string.Empty;
            // "confirmacao_pendente" → IA entendeu tudo, aguarda sim/não
            // "pergunta_info"        → IA precisa de mais informação
            // "pergunta"             → usuário fez uma pergunta, IA respondeu
            // "erro"                 → IA não conseguiu entender após 2 tentativas

            public string Mensagem { get; set; } = string.Empty;
            // O texto que aparece no chat para o usuário

            public Transacao? TransacaoPendente { get; set; }
            // Preenchido só quando tipo == "confirmacao_pendente"
            // Flutter guarda isso para mandar ao /confirmar quando usuário digitar "sim"
        }

       private string MontarPrompt(string textoUsuario, List<MensagemHistorico> historico, string categoriasExistentes)
        {
            string dataAtual = DateTime.Now.ToString("yyyy-MM-dd");

            // Monta o histórico da conversa em texto
            // para a IA entender o contexto do que já foi dito
            var historicoTexto = string.Join("\n", historico
                .Select(h => $"{(h.Role == "user" ? "Usuário" : "Assistente")}: {h.Content}"));

            return $@"
                Você é um assistente financeiro pessoal chamado RegistrAI.
                A data de HOJE é {dataAtual}.

                Você recebe mensagens de texto e deve classificá-las em 3 tipos:

                ════════════════════════════════════
                TIPO 1 — REGISTRO DE TRANSAÇÃO
                ════════════════════════════════════
                O usuário quer registrar uma despesa ou receita.
                Exemplos: 'gastei 50 no almoço', 'recebi meu salário de 3000'

                Verifique se tem TODOS esses campos:
                - valor (número)
                - categoria (assunto do gasto/receita)
                - tipo ('Despesa' ou 'Receita')
                - data (OBRIGATÓRIO: se o usuário não mencionar nenhuma data, use SEMPRE a data de hoje: {dataAtual}.
                  Se o usuário mencionar uma data específica — como 'dia 14/06', '14/06/2026', '12 de maio', 'dia 3' —
                  use ESSA data informada, e não a de hoje. Assuma o ano atual, a menos que o usuário diga outro ano.
                  NUNCA pergunte a data.)

                IMPORTANTE — como identificar o TIPO automaticamente pelo verbo usado:
                Use 'Despesa' quando o usuário usar verbos como: comprei, gastei, paguei, comprando, gasto.
                Use 'Receita' quando o usuário usar verbos como: recebi, ganhei, faturei, vendi, recebimento.
                NUNCA pergunte ao usuário se é despesa ou receita — infira pelo contexto da frase.
                Só pergunte o tipo se a frase for genuinamente ambígua e não tiver nenhum verbo claro (ex: apenas '50 reais mercado').

                IMPORTANTE — como identificar a CATEGORIA automaticamente quando a própria frase já contém a palavra:
                Se o usuário mencionar uma palavra que já é (ou corresponde diretamente a) uma das categorias permitidas
                — como 'salário', 'aluguel', 'condomínio', 'internet', 'academia', 'mercado' —
                use essa categoria diretamente, SEM perguntar.
                Só pergunte a categoria se a frase for genuinamente vaga (ex: 'gastei 50 reais', sem nenhuma pista de assunto).

                Se tiver TUDO → retorne APENAS este JSON:
                {{
                    ""classificacao"": ""confirmacao_pendente"",
                    ""mensagem"": ""Vou registrar R$[valor] em [categoria] em [data formatada dd/MM/yyyy]. Confirma?"",
                    ""transacao"": {{
                        ""valor"": (número decimal com ponto),
                        ""categoria"": (texto curto),
                        ""data"": (formato ISO: YYYY-MM-DDTHH:mm:ss),
                        ""tipo"": (apenas 'Despesa' ou 'Receita')
                    }}
                }}

                Se faltar algum campo → retorne APENAS este JSON:
                {{
                    ""classificacao"": ""pergunta_info"",
                    ""mensagem"": ""[pergunta objetiva sobre o dado que falta]"",
                    ""transacao"": null
                }}

                ════════════════════════════════════
                TIPO 2 — PERGUNTA SOBRE FINANÇAS
                ════════════════════════════════════
                O usuário quer saber informações sobre seus gastos.
                Exemplos: 'quanto gastei essa semana?', 'qual meu saldo de maio?'

                Retorne APENAS este JSON:
                {{
                    ""classificacao"": ""pergunta"",
                    ""filtros"": {{
                        ""periodo"": (""hoje"", ""semana"", ""mes"", ""mes_passado"", ""ano"" ou null),
                        ""categoria"": (categoria mencionada ou null),
                        ""tipo"": (""Despesa"", ""Receita"" ou null),
                        ""dataInicio"": (formato ISO YYYY-MM-DD. Preencha SEMPRE que o usuário mencionar um mês
                          específico por nome, como 'junho', 'em maio', 'no mês de março' — use o primeiro dia
                          desse mês. Assuma o ano atual, a menos que o usuário diga outro ano. Null se o usuário
                          não mencionou nenhum mês/data específica.),
                        ""dataFim"": (formato ISO YYYY-MM-DD. Preencha junto com dataInicio, usando o último dia
                          do mês mencionado. Null se não houver menção de data específica.)
                    }},
                    ""mensagem"": null,
                    ""transacao"": null
                }}

                ════════════════════════════════════
                TIPO 3 — CONFIRMAÇÃO
                ════════════════════════════════════
                O usuário está respondendo à pergunta de confirmação feita pelo assistente
                (verifique o HISTÓRICO DA CONVERSA: a última mensagem do assistente foi uma pergunta tipo 'Confirma?').

                Se a resposta expressar CONCORDÂNCIA/AFIRMAÇÃO com o que foi perguntado
                — exemplos: 'sim', 'confirmo', 'pode salvar', 'isso', 'isso mesmo', 'exato', 'é isso', 'confere', 'certo', 'ok', 'positivo' —
                trate como confirmado.

                Se a resposta expressar NEGAÇÃO/CANCELAMENTO
                — exemplos: 'não', 'cancela', 'para', 'errado', 'não é isso' —
                trate como cancelado.

                Se confirmou → retorne APENAS este JSON:
                {{
                    ""classificacao"": ""confirmado"",
                    ""mensagem"": null,
                    ""transacao"": null
                }}

                Se cancelou → retorne APENAS este JSON:
                {{
                    ""classificacao"": ""cancelado"",
                    ""mensagem"": ""Ok, a transação não foi registrada."",
                    ""transacao"": null
                }}

                ════════════════════════════════════
                TIPO 4 — EXCLUSÃO DE TRANSAÇÃO
                ════════════════════════════════════
                O usuário quer excluir uma transação já registrada.
                Exemplos: 'excluir compra de bergamota do dia 12', 
                        'apagar gasto de 90 reais em calçados',
                        'deletar transação de alimentação de ontem'

                Retorne APENAS este JSON:
                {{
                    ""classificacao"": ""exclusao_pendente"",
                    ""filtros_exclusao"": {{
                        ""categoria"": (categoria mencionada ou null),
                        ""valor"": (valor mencionado como número ou null),
                        ""data"": (data mencionada no formato YYYY-MM-DD ou null),
                        ""tipo"": (""Despesa"" ou ""Receita"" ou null)
                    }},
                    ""mensagem"": null,
                    ""transacao"": null
                }}

                ════════════════════════════════════
                REGRAS IMPORTANTES
                ════════════════════════════════════
                - Retorne SEMPRE apenas JSON válido, sem markdown, sem texto fora do JSON
                - Se o usuário falar 'ontem', subtraia 1 dia de {dataAtual}
                - Seja objetivo nas perguntas: 'Qual o valor?' e não 'Poderia me informar o valor?'
                - Quando existir uma categoria MAIS ESPECÍFICA na lista permitida que já cobre o caso
                    (ex: 'Estacionamento', 'Combustível', 'Pedágio' dentro do grupo de Transporte;
                    'Farmácia', 'Consulta' dentro do grupo de Saúde), use ESSA categoria específica,
                    e não a categoria mais genérica do mesmo grupo.
                    ERRADO: gasto em estacionamento → categoria 'Transporte'
                    CERTO: gasto em estacionamento → categoria 'Estacionamento'
                - A categoria NUNCA deve ser o nome do produto, marca ou item comprado
                    (isso é diferente de escolher entre categorias específicas ou genéricas da lista — veja a regra anterior).
                    ERRADO: 'Bergamota', 'Heineken', 'Tênis Nike', 'Pizza'
                    CERTO: 'Alimentação', 'Bebidas', 'Vestuário'
                 - CATEGORIAS PERMITIDAS:
                  Despesas: 
                    Alimentação, Mercado, Restaurante, Delivery, Bebidas,
                    Transporte, Combustível, Estacionamento, Pedágio,
                    Moradia, Aluguel, Condomínio, Energia, Água, Internet, Gás,
                    Saúde, Farmácia, Consulta, Plano de Saúde, Academia,
                    Educação, Material Escolar,
                    Vestuário, Calçados, Acessórios,
                    Lazer, Cinema, Viagem, Hobby, Show, Jogos,
                    Pets, Ração, Veterinário,
                    Assinaturas, Streaming, Aplicativos,
                    Beleza, Salão, Cosméticos, Barbearia,
                    Tecnologia, Eletrônicos, Informática,
                    Casa, Móveis, Decoração, Reforma, Eletrodomésticos,
                    Presentes, Doações,
                    Impostos, Taxas, Multas,
                    Outros

                  Receitas:
                    Salário, Décimo Terceiro, Férias, Bônus,
                    Freelance, Serviços, Consultoria,
                    Investimentos, Dividendos, Rendimentos, Aluguel Recebido,
                    Vendas, Reembolso,
                    Presente, Mesada,
                    Outros

                - REGRA OBRIGATÓRIA: Use SEMPRE uma categoria da lista acima.
                  NUNCA crie categorias fora dessa lista.
                  NUNCA use o nome do produto como categoria.
                  Escolha a categoria mais próxima do contexto.
                  Em caso de dúvida, use 'Outros'.
                ════════════════════════════════════
                HISTÓRICO DA CONVERSA
                ════════════════════════════════════
                {(string.IsNullOrEmpty(historicoTexto) ? "Nenhum histórico ainda." : historicoTexto)}

                Mensagem atual do usuário: '{textoUsuario}'";

                
            }

        // ═══════════════════════════════════════
        // MÉTODO PRIVADO — CHAMA A OPENAI
        // ═══════════════════════════════════════
        private async Task<string> ChamarOpenAI(string prompt)
        {
            var corpoRequisicao = new
            {
                model = "gpt-4o-mini",
                messages = new[]
                {
                    new { role = "user", content = prompt }
                }
            };

            var conteudo = new StringContent(
                JsonSerializer.Serialize(corpoRequisicao),
                Encoding.UTF8,
                "application/json"
            );

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");

            var resposta = await _httpClient.PostAsync(
                "https://api.openai.com/v1/chat/completions",
                conteudo
            );

            var jsonResposta = await resposta.Content.ReadAsStringAsync();

            if (!resposta.IsSuccessStatusCode)
                throw new Exception($"Erro OpenAI: {jsonResposta}");

            using var documento = JsonDocument.Parse(jsonResposta);
            return documento.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()!;
        }

        // ═══════════════════════════════════════
        // ENDPOINT 1 — INTERPRETAR
        // ═══════════════════════════════════════
        [HttpPost("interpretar")]
        public async Task<IActionResult> Interpretar([FromBody] RequisicaoChat requisicao)
        {
            // VERIFICAÇÃO DO LIMITE DE TENTATIVAS
            // Se o Flutter já mandou 2 tentativas sem sucesso,
            // nem chama a IA — retorna erro direto
            if (requisicao.Tentativas >= 2)
            {
                return Ok(new RespostaChat
                {
                    Tipo = "erro",
                    Mensagem = "Não consegui entender. Tente enviar assim: 'Gastei R$50 em alimentação hoje'",
                    TransacaoPendente = null
                });
            }

            // Busca categorias já usadas no banco
            var categoriasDespesas = await _context.Transacoes
                .Where(t => t.DispositivoId == DispositivoIdAtual && t.Tipo == "Despesa")
                .Select(t => t.Categoria)
                .Distinct()
                .ToListAsync();

            var categoriasReceitas = await _context.Transacoes
                .Where(t => t.DispositivoId == DispositivoIdAtual && t.Tipo == "Receita")
                .Select(t => t.Categoria)
                .Distinct()
                .ToListAsync();

            var categoriasExistentes = categoriasDespesas.Any() || categoriasReceitas.Any()
                ? $"Despesas: {(categoriasDespesas.Any() ? string.Join(", ", categoriasDespesas) : "nenhuma ainda")}\n" +
                $"Receitas: {(categoriasReceitas.Any() ? string.Join(", ", categoriasReceitas) : "nenhuma ainda")}"
                : "Nenhuma categoria cadastrada ainda — crie categorias curtas e claras.";

            string prompt = MontarPrompt(requisicao.Texto, requisicao.Historico, categoriasExistentes);

            string textoIa;
            try
            {
                textoIa = await ChamarOpenAI(prompt);
                textoIa = textoIa.Replace("```json", "").Replace("```", "").Trim();
                Console.WriteLine($"RESPOSTA DA IA: {textoIa}");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Erro ao chamar a IA: {ex.Message}");
            }

            // CONVERTE O JSON DA IA PARA UM OBJETO C#
            using var respostaIa = JsonDocument.Parse(textoIa);
            var classificacao = respostaIa.RootElement
                .GetProperty("classificacao").GetString();

            // ROTEAMENTO — decide o que fazer baseado na classificação da IA
            switch (classificacao)
            {
                // USUÁRIO QUER REGISTRAR E TEM TUDO NECESSÁRIO
                case "confirmacao_pendente":
                    var transacaoJson = respostaIa.RootElement
                        .GetProperty("transacao").ToString();

                    var transacaoPendente = JsonSerializer.Deserialize<Transacao>(
                        transacaoJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                    );

                    // Normaliza o Tipo (mesma lógica que já tínhamos)
                    var tipoNormalizado = transacaoPendente!.Tipo?.Trim().ToLower();
                    transacaoPendente.Tipo = char.ToUpper(tipoNormalizado![0]) + tipoNormalizado[1..];
                    transacaoPendente.DispositivoId = DispositivoIdAtual;

                    var mensagemConfirmacao = $"Vou registrar R${transacaoPendente.Valor.ToString("N2", _culturaBr)} em {transacaoPendente.Categoria} em {transacaoPendente.Data:dd/MM/yyyy}. Confirma?";

                    return Ok(new RespostaChat
                    {
                        Tipo = "confirmacao_pendente",
                        Mensagem = mensagemConfirmacao!,
                        TransacaoPendente = transacaoPendente
                    });

                // IA PRECISA DE MAIS INFORMAÇÃO
                case "pergunta_info":
                    var pergunta = respostaIa.RootElement
                        .GetProperty("mensagem").GetString();

                    return Ok(new RespostaChat
                    {
                        Tipo = "pergunta_info",
                        Mensagem = pergunta!,
                        TransacaoPendente = null
                    });

                // USUÁRIO FEZ UMA PERGUNTA SOBRE SEUS GASTOS
                case "pergunta":
                {
                    var filtrosElement = respostaIa.RootElement.GetProperty("filtros");

                    // Extrai os filtros que a IA identificou na pergunta
                    var periodo = filtrosElement.TryGetProperty("periodo", out var p) ? p.GetString() : null;
                    var categoria = filtrosElement.TryGetProperty("categoria", out var c) ? c.GetString() : null;
                    var tipo = filtrosElement.TryGetProperty("tipo", out var tp) ? tp.GetString() : null;
                    var dataInicioStr = filtrosElement.TryGetProperty("dataInicio", out var di) && di.ValueKind == JsonValueKind.String ? di.GetString() : null;
                    var dataFimStr = filtrosElement.TryGetProperty("dataFim", out var df) && df.ValueKind == JsonValueKind.String ? df.GetString() : null;

                    // Calcula as datas baseado no período
                    var hoje = DateTime.Today;
                    DateTime dataInicio;
                    DateTime dataFim = hoje;

                    switch (periodo)
                    {
                        case "hoje":
                            dataInicio = hoje;
                            break;
                        case "semana":
                            var diasDesdeSegunda = ((int)hoje.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
                            dataInicio = hoje.AddDays(-diasDesdeSegunda);
                            break;
                        case "mes_passado":
                            var mesPassado = hoje.AddMonths(-1);
                            dataInicio = new DateTime(mesPassado.Year, mesPassado.Month, 1);
                            dataFim = dataInicio.AddMonths(1).AddDays(-1);
                            break;
                        case "ano":
                            dataInicio = new DateTime(hoje.Year, 1, 1);
                            break;
                        default: // mes
                            dataInicio = new DateTime(hoje.Year, hoje.Month, 1);
                            break;
                    }

                    // Se a IA identificou um mês/data específica (ex: "junho"), isso tem prioridade sobre o cálculo acima
                    if (DateTime.TryParse(dataInicioStr, out var dataInicioEspecifica))
                        dataInicio = dataInicioEspecifica.Date;

                    if (DateTime.TryParse(dataFimStr, out var dataFimEspecifica))
                        dataFim = dataFimEspecifica.Date;

                    // Consulta o banco com os filtros
                    var query = _context.Transacoes.Where(t => t.DispositivoId == DispositivoIdAtual);
                    query = query.Where(t => t.Data >= dataInicio && t.Data <= dataFim);

                    if (!string.IsNullOrEmpty(categoria))
                        query = query.Where(t => t.Categoria.ToLower().Contains(categoria.ToLower()));

                    if (!string.IsNullOrEmpty(tipo))
                        query = query.Where(t => t.Tipo == tipo);

                    var transacoesFiltradas = await query.ToListAsync();

                    // Monta o resumo para a IA formatar a resposta
                    var totalReceitasConsulta = transacoesFiltradas
                        .Where(t => t.Tipo == "Receita")
                        .Sum(t => t.Valor);

                    var totalDespesasConsulta = transacoesFiltradas
                        .Where(t => t.Tipo == "Despesa")
                        .Sum(t => t.Valor);

                    var saldoConsulta = totalReceitasConsulta - totalDespesasConsulta;

                    var quantidadeConsulta = transacoesFiltradas.Count;

                    var porCategoriaConsulta = transacoesFiltradas
                        .GroupBy(t => t.Categoria)
                        .Select(g => new { Categoria = g.Key, Total = g.Sum(t => t.Valor) })
                        .OrderByDescending(g => g.Total)
                        .ToList();

                    var transacoesDetalhadas = transacoesFiltradas
                        .Where(t => string.IsNullOrEmpty(tipo) || t.Tipo == tipo)
                        .OrderByDescending(t => t.Data)
                        .Select(t => new {
                            Data = t.Data.ToString("dd/MM/yyyy"),
                            t.Categoria,
                            t.Tipo,
                            Valor = $"R${t.Valor.ToString("N2", _culturaBr)}"
                        }).ToList();

                    string promptResposta = $@"
                        O usuário perguntou: '{requisicao.Texto}'
                        Os dados encontrados no banco foram:
                        - Total Receitas: R${totalReceitasConsulta.ToString("N2", _culturaBr)}
                        - Total Despesas: R${totalDespesasConsulta.ToString("N2", _culturaBr)}
                        - Saldo: R${saldoConsulta.ToString("N2", _culturaBr)}
                        - Quantidade de transações: {quantidadeConsulta}
                        - Por categoria: {JsonSerializer.Serialize(porCategoriaConsulta)}
                        - Período: de {dataInicio:dd/MM/yyyy} até {dataFim:dd/MM/yyyy}
                        - Lista completa de transações: {JsonSerializer.Serialize(transacoesDetalhadas)}
                        - Filtro de tipo aplicado: {(string.IsNullOrEmpty(tipo) ? "nenhum (todas)" : tipo)}

                        IMPORTANTE: Se o usuário pediu para LISTAR ou VER transações,
                        liste APENAS as transações da lista acima — não invente nem adicione outras.
                        Se foi aplicado filtro de tipo, liste SOMENTE as do tipo filtrado.
                        Formato de listagem: '• [data] - [categoria]: [valor]'
                        
                        Para perguntas de RESUMO ou SALDO, responda de forma humanizada e resumida.
                        Não use markdown. Máximo 2 linhas para resumos.";

                    var respostaHumanizada = await ChamarOpenAI(promptResposta);

                    return Ok(new RespostaChat
                    {
                        Tipo = "pergunta",
                        Mensagem = respostaHumanizada!.Trim(),
                        TransacaoPendente = null
                    });
                }

                case "exclusao_pendente":
                {
                    var filtrosExclusao = respostaIa.RootElement.GetProperty("filtros_exclusao");

                    // Extrai os filtros que a IA identificou
                    var catExclusao = filtrosExclusao.TryGetProperty("categoria", out var ce) ? ce.GetString() : null;
                    var valorExclusao = filtrosExclusao.TryGetProperty("valor", out var ve) ? (ve.ValueKind == JsonValueKind.Number ? ve.GetDecimal() : (decimal?)null) : null;
                    var dataExclusao = filtrosExclusao.TryGetProperty("data", out var de) ? de.GetString() : null;
                    var tipoExclusao = filtrosExclusao.TryGetProperty("tipo", out var te) ? te.GetString() : null;

                    // Busca no banco com os filtros
                     var queryExclusao = _context.Transacoes.Where(t => t.DispositivoId == DispositivoIdAtual);

                    if (!string.IsNullOrEmpty(catExclusao))
                        queryExclusao = queryExclusao.Where(t => t.Categoria.ToLower().Contains(catExclusao.ToLower()));

                    if (valorExclusao.HasValue)
                        queryExclusao = queryExclusao.Where(t => t.Valor == valorExclusao.Value);

                    if (!string.IsNullOrEmpty(dataExclusao) && DateTime.TryParse(dataExclusao, out var dataParsed))
                        queryExclusao = queryExclusao.Where(t => t.Data.Date == dataParsed.Date);

                    if (!string.IsNullOrEmpty(tipoExclusao))
                        queryExclusao = queryExclusao.Where(t => t.Tipo == tipoExclusao);

                    var transacoesEncontradas = await queryExclusao.ToListAsync();

                    // Nenhuma encontrada
                    if (!transacoesEncontradas.Any())
                    {
                        return Ok(new RespostaChat
                        {
                            Tipo = "erro",
                            Mensagem = "Não encontrei nenhuma transação com essas características. Tente ser mais específico.",
                            TransacaoPendente = null
                        });
                    }

                    // Encontrou exatamente 1 — pede confirmação
                    if (transacoesEncontradas.Count == 1)
                    {
                        var t = transacoesEncontradas[0];
                        return Ok(new RespostaChat
                        {
                            Tipo = "exclusao_confirmacao",
                            Mensagem = $"Encontrei: {t.Tipo} de R${t.Valor.ToString("N2", _culturaBr)} em {t.Categoria} em {t.Data:dd/MM/yyyy}. Deseja excluir?",
                            TransacaoPendente = t // reutilizamos para guardar o ID
                        });
                    }

                    // Encontrou mais de 1 — lista para o usuário escolher
                    var lista = string.Join("\n", transacoesEncontradas
                        .Select((t, i) => $"• {i + 1}. R${t.Valor.ToString("N2", _culturaBr)} em {t.Categoria} em {t.Data:dd/MM/yyyy}"));

                    return Ok(new RespostaChat
                    {
                        Tipo = "exclusao_multipla",
                        Mensagem = $"Encontrei {transacoesEncontradas.Count} transações. Qual deseja excluir?\n{lista}",
                        TransacaoPendente = null
                    });
                }

                    // USUÁRIO CANCELOU
                case "cancelado":
                    return Ok(new RespostaChat
                    {
                        Tipo = "cancelado",
                        Mensagem = "Ok, a transação não foi registrada.",
                        TransacaoPendente = null
                    });
                case "confirmado":
                    return Ok(new RespostaChat
                    {
                        Tipo = "confirmado",
                        Mensagem = "",
                        TransacaoPendente = null
                    });

                // CLASSIFICAÇÃO DESCONHECIDA
                default:
                    return Ok(new RespostaChat
                    {
                        Tipo = "erro",
                        Mensagem = "Não consegui entender. Tente enviar assim: 'Gastei R$50 em alimentação hoje'",
                        TransacaoPendente = null
                    });
            } 

        }

        // ═══════════════════════════════════════
        // ENDPOINT 2 — CONFIRMAR
        // ═══════════════════════════════════════
        [HttpPost("confirmar")]
        public async Task<IActionResult> Confirmar([FromBody] Transacao transacao)
        {
            // O Flutter manda a transacaoPendente que guardou na memória
            // quando a IA retornou "confirmacao_pendente"

            if (transacao == null)
                return BadRequest("Nenhuma transação recebida.");

            if (string.IsNullOrWhiteSpace(DispositivoIdAtual))
                return BadRequest("Cabeçalho X-Device-Id é obrigatório.");

            // Normaliza o Tipo por segurança (mesma lógica dos outros endpoints)
            var tipoNormalizado = transacao.Tipo?.Trim().ToLower();

            if (tipoNormalizado != "receita" && tipoNormalizado != "despesa")
                return BadRequest($"Tipo inválido: '{transacao.Tipo}'.");

            transacao.Tipo = char.ToUpper(tipoNormalizado[0]) + tipoNormalizado[1..];
            transacao.DispositivoId = DispositivoIdAtual;

            // Salva no banco
            _context.Transacoes.Add(transacao);
            await _context.SaveChangesAsync();

            return Ok(new RespostaChat
            {
                Tipo = "salvo",
                Mensagem = $"✅ {transacao.Tipo} de R${transacao.Valor.ToString("N2", _culturaBr)} em {transacao.Categoria} registrada com sucesso!",
                TransacaoPendente = null
            });
        }

        // ═══════════════════════════════════════
        // ENDPOINT 3 — EXCLUIR
        // ═══════════════════════════════════════
        [HttpDelete("excluir/{id}")]
        public async Task<IActionResult> Excluir(int id)
        {
            var transacao = await _context.Transacoes
                .FirstOrDefaultAsync(t => t.Id == id && t.DispositivoId == DispositivoIdAtual);

            if (transacao == null)
                return NotFound("Transação não encontrada.");

            _context.Transacoes.Remove(transacao);
            await _context.SaveChangesAsync();

            return Ok(new RespostaChat
            {
                Tipo = "excluido",
                Mensagem = $"✅ {transacao.Tipo} de R${transacao.Valor.ToString("N2", _culturaBr)} em {transacao.Categoria} excluída com sucesso!",
                TransacaoPendente = null
            });
        }

        [HttpDelete("reset")]
        public async Task<IActionResult> Reset()
        {
            var minhasTransacoes = _context.Transacoes.Where(t => t.DispositivoId == DispositivoIdAtual);
            _context.Transacoes.RemoveRange(minhasTransacoes);
            await _context.SaveChangesAsync();
            return Ok("Todas as suas transações foram removidas.");
        }
    }
}