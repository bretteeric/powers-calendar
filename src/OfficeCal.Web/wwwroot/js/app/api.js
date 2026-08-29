// 全站共用的 Axios 實例與錯誤處理。頁面一律透過 window.api 呼叫後端。
(function () {
  const http = axios.create({ baseURL: '/', withCredentials: true });

  // 規格 9：409 顯示衝突明細、401 導向登入頁、其餘以 SweetAlert2 顯示 message
  http.interceptors.response.use(
    (res) => res,
    (err) => {
      const status = err.response ? err.response.status : 0;
      const body = err.response ? err.response.data : null;

      if (status === 401) {
        window.location.href = '/Login?returnUrl=' + encodeURIComponent(window.location.pathname);
        return new Promise(() => {});   // 停在這裡，不要讓呼叫端再處理
      }

      if (status === 409 && body && body.data && body.data.conflicts) {
        showConflicts(body.message, body.data.conflicts);
        return Promise.reject(err);
      }

      if (status === 0) {
        Swal.fire('連線失敗', '無法連線到伺服器，請檢查網路後再試', 'error');
      } else {
        const errors = (body && body.errors && body.errors.length)
          ? '<ul class="text-start small mb-0">'
            + body.errors.map((e) => '<li>' + escapeHtml(e) + '</li>').join('') + '</ul>'
          : '';
        Swal.fire({
          icon: 'error',
          title: '操作失敗',
          html: escapeHtml((body && body.message) || '發生未預期的錯誤') + errors,
        });
      }
      return Promise.reject(err);
    }
  );

  function escapeHtml(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  function fmt(iso) {
    const d = new Date(iso);
    const p = (n) => String(n).padStart(2, '0');
    return `${d.getMonth() + 1}/${d.getDate()} ${p(d.getHours())}:${p(d.getMinutes())}`;
  }

  function showConflicts(message, conflicts) {
    const rows = conflicts.map((c) => `
      <tr>
        <td class="text-nowrap">${escapeHtml(c.roomName)}</td>
        <td class="text-nowrap">${fmt(c.startAt)} – ${fmt(c.endAt).split(' ')[1]}</td>
        <td>${escapeHtml(c.title)}</td>
        <td class="text-nowrap">${escapeHtml(c.ownerName)}</td>
      </tr>`).join('');

    Swal.fire({
      icon: 'warning',
      title: message || '會議廳於下列時段已被預約',
      width: 640,
      html: `<div class="table-responsive"><table class="table table-sm align-middle mb-0">
               <thead><tr><th>會議廳</th><th>時段</th><th>事件</th><th>預約人</th></tr></thead>
               <tbody>${rows}</tbody></table></div>
             <p class="small text-muted mt-2 mb-0">整筆預約未寫入，請調整時段後重新送出。</p>`,
    });
  }

  // 統一拆信封：成功時回傳 data，失敗時已由攔截器處理
  async function unwrap(promise) {
    const res = await promise;
    return res.data.data;
  }

  window.api = {
    http,
    escapeHtml,
    fmtDateTime: fmt,
    get: (url, config) => unwrap(http.get(url, config)),
    post: (url, body, config) => unwrap(http.post(url, body, config)),
    put: (url, body, config) => unwrap(http.put(url, body, config)),
    del: (url, config) => unwrap(http.delete(url, config)),

    /** Date → 'YYYY-MM-DDTHH:mm:ss'，不做時區轉換（全系統為台北當地時間）。 */
    toLocalIso(d) {
      const p = (n) => String(n).padStart(2, '0');
      return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`
           + `T${p(d.getHours())}:${p(d.getMinutes())}:00`;
    },
    /** 'YYYY-MM-DD' → Date（當地時間 00:00）。 */
    parseDate(s) {
      const [y, m, d] = s.split('-').map(Number);
      return new Date(y, m - 1, d);
    },
  };
})();
