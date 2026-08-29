(function () {
  window.SettingsPage = {
    data() {
      return {
        me: null,
        pwd: { currentPassword: '', newPassword: '', confirm: '' },
        saving: false,
      };
    },
    mounted() { window.api.get('/api/v1/me').then((m) => { this.me = m; }); },
    methods: {
      async copyFeed() {
        try {
          await navigator.clipboard.writeText(this.me.feedUrl);
          Swal.fire({ icon: 'success', title: '已複製訂閱網址', timer: 1200,
                      showConfirmButton: false });
        } catch (e) {
          Swal.fire('請手動複製', this.me.feedUrl, 'info');
        }
      },
      async resetToken() {
        const ok = await Swal.fire({
          icon: 'warning', title: '重新產生訂閱網址？',
          text: '舊網址會立刻失效，已訂閱的行事曆軟體需要重新加入。',
          showCancelButton: true, confirmButtonText: '重新產生', cancelButtonText: '取消',
        });
        if (!ok.isConfirmed) return;
        const data = await window.api.post('/api/v1/me/reset-feed-token');
        this.me.feedUrl = data.feedUrl;
        Swal.fire({ icon: 'success', title: '已重新產生', timer: 1400, showConfirmButton: false });
      },
      async changePassword() {
        if (this.pwd.newPassword !== this.pwd.confirm) {
          Swal.fire('兩次輸入的新密碼不一致', '', 'info');
          return;
        }
        this.saving = true;
        try {
          await window.api.post('/api/v1/me/change-password', {
            currentPassword: this.pwd.currentPassword,
            newPassword: this.pwd.newPassword,
          });
          this.pwd = { currentPassword: '', newPassword: '', confirm: '' };
          Swal.fire({ icon: 'success', title: '密碼已更新', timer: 1400, showConfirmButton: false });
        } catch (e) {
          // 攔截器已顯示訊息
        } finally {
          this.saving = false;
        }
      },
    },
    template: `
<div class="row g-4" v-if="me">
  <div class="col-lg-6">
    <div class="card-oc p-4 h-100">
      <h2 class="h6 mb-3">個人資料</h2>
      <dl class="row small mb-0">
        <dt class="col-4">員工編號</dt><dd class="col-8">{{ me.employeeNo }}</dd>
        <dt class="col-4">姓名</dt><dd class="col-8">{{ me.displayName }}</dd>
        <dt class="col-4">Email</dt><dd class="col-8">{{ me.email }}</dd>
        <dt class="col-4">部門</dt><dd class="col-8">{{ me.departmentName || '未指定' }}</dd>
        <dt class="col-4">角色</dt>
        <dd class="col-8">{{ me.isAdmin ? '系統管理員' : '一般員工' }}</dd>
      </dl>

      <hr />
      <h2 class="h6 mb-2">訂閱行事曆</h2>
      <p class="small text-muted">
        把下面的網址加進 Outlook 或 Google 行事曆的「訂閱」功能，即可自動同步你的行程。
      </p>
      <div class="input-group input-group-sm mb-2">
        <input class="form-control" :value="me.feedUrl" readonly />
        <button class="btn btn-outline-secondary" @click="copyFeed">複製</button>
      </div>
      <button class="btn btn-outline-danger btn-sm" @click="resetToken">重新產生訂閱網址</button>
    </div>
  </div>

  <div class="col-lg-6">
    <div class="card-oc p-4 h-100">
      <h2 class="h6 mb-3">修改密碼</h2>
      <form @submit.prevent="changePassword">
        <input type="text" class="d-none" :value="me.employeeNo" autocomplete="username" readonly />
        <div class="mb-3">
          <label class="form-label small">目前密碼</label>
          <input type="password" class="form-control" v-model="pwd.currentPassword"
                 autocomplete="current-password" />
        </div>
        <div class="mb-3">
          <label class="form-label small">新密碼（至少 8 個字元）</label>
          <input type="password" class="form-control" v-model="pwd.newPassword"
                 autocomplete="new-password" />
        </div>
        <div class="mb-3">
          <label class="form-label small">再輸入一次新密碼</label>
          <input type="password" class="form-control" v-model="pwd.confirm"
                 autocomplete="new-password" />
        </div>
        <button class="btn btn-primary" :disabled="saving">
          {{ saving ? '更新中…' : '更新密碼' }}
        </button>
      </form>
    </div>
  </div>
</div>`,
  };
})();
