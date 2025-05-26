(() => {
    let registos = [], page = 0, pageSize = 10;

    const $ = s => document.querySelector(s);
    const navs = {
        reg: $('#navRegistos'),
        ana: $('#navAnalises'),
        man: $('#navManual')
    };
    const tabs = {
        reg: $('#tabRegistos'),
        ana: $('#tabAnalises'),
        man: $('#tabManual')
    };

    // navegação entre tabs
    Object.entries(navs).forEach(([k, el]) => {
        el.onclick = e => {
            e.preventDefault();
            Object.values(navs).forEach(n => n.classList.remove('active'));
            Object.values(tabs).forEach(t => t.style.display = 'none');
            el.classList.add('active');
            tabs[k].style.display = 'block';
        };
    });

    // carregar registos
    async function loadRegistos() {
        const resp = await fetch('/registos');
        registos = await resp.json();
        page = 0; renderRegistos();
    }

    function renderRegistos() {
        const tbody = $('#tblRegistosBody');
        tbody.innerHTML = '';
        const filtroW = $('#fWavy').value.trim().toUpperCase();
        const filtroM = $('#fTipoMsg').value;
        const lista = registos
            .filter(r => (!filtroW || r.wavyId === filtroW))
            .filter(r => (!filtroM || r.tipoMensagem === filtroM));
        const start = page * pageSize;
        const pageData = lista.slice(start, start + pageSize);
        for (let r of pageData) {
            const tr = document.createElement('tr');
            tr.innerHTML = `
        <td>${new Date(r.timestamp).toLocaleString()}</td>
        <td>${r.wavyId || '-'}</td>
        <td>${r.tipoDado || '-'}</td>
        <td>${r.valor ?? '-'}</td>`;
            tbody.appendChild(tr);
        }
        $('#pageInfo').textContent = `Página ${page + 1} de ${Math.ceil(lista.length / pageSize)}`;
    }

    $('#filtrosRegistos').onsubmit = e => {
        e.preventDefault();
        page = 0; renderRegistos();
    };
    $('#btnPrev').onclick = () => { if (page > 0) page--, renderRegistos(); };
    $('#btnNext').onclick = () => { page++; renderRegistos(); };

    // carregar análises
    async function loadAnalises() {
        // usa um intervalo grande para trazer todas
        const di = '2000-01-01', df = new Date().toISOString();
        const resp = await fetch(`/analises?sensor=Temperatura&di=${di}&df=${df}`);
        const data = await resp.json();
        const tbody = $('#tblAnalisesBody');
        tbody.innerHTML = '';
        data.forEach(r => {
            const tr = document.createElement('tr');
            tr.innerHTML = `
        <td>${new Date(r.timestamp).toLocaleString()}</td>
        <td>${r.tipoDado}</td>
        <td>${r.media.toFixed(1)}</td>
        <td>${r.desvioPadrao.toFixed(2)}</td>`;
            tbody.appendChild(tr);
        });
    }

    // análise manual
    $('#formManual').onsubmit = async e => {
        e.preventDefault();
        const sensor = $('#mSensor').value;
        const di = $('#mDi').value;
        const df = $('#mDf').value;
        const res = await fetch(`/analise/manual?sensor=${sensor}&di=${di}&df=${df}`, { method: 'POST' });
        const js = await res.json();
        const $r = $('#mResult');
        $r.classList.remove('d-none', 'alert-info', 'alert-danger');
        if (res.ok) {
            $r.classList.add('alert-success');
            $r.textContent = `Média=${js.media.toFixed(2)}, Desvio=${js.desvioPadrao.toFixed(2)}`;
            // recarrega as análises
            loadAnalises();
        }
        else {
            $r.classList.add('alert-danger');
            $r.textContent = `Erro: ${js}`;
        }
    };

    // inicia tudo
    loadRegistos();
    loadAnalises();
})();
