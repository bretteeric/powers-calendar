(function () {
  window.AdminUsersPage = {
    data() {
      return { users: [], departments: [], editing: null, saving: false, modal: null };
    },
    mounted() {
      this.modal = new bootstrap.Modal(this.$refs.dialog);
      window.api.get('/api/v1/departments').then((d) => { this.departments = d; });
      this.load();
    },
    methods: {
      async load() { this.users = await window.api.get('/api/v1/users'); },
      blank() {
        return {
          id: null, employeeNo: '', displayName: '', email: '',
          departmentId: null, role: 'Employee', isActive: true, password: '',
        };
      },
      openCreate() { this.editing = this.blank(); this.modal.show(); },
      openEdit(u) { this.editing = Object.assign(this.blank(), u); this.modal.show(); },
      async save() {
        this.saving = true;
        try {
          if (this.editing.id) {
            await window.api.put('/api/v1/users/' + this.editing.id, {
              displayName: this.editing.displayName, email: this.editing.email,
              departmentId: this.editing.departmentId, role: this.editing.role,
              isActive: this.editing.isActive,
            });
          } else {
            await window.api.post('/api/v1/users', {
              employeeNo: this.editing.employeeNo, displayName: this.editing.displayName,
              email: this.editing.email, departmentId: this.editing.departmentId,
              role: this.editing.role, password: this.editing.password,
            });
          }
          this.modal.hide();
          await this.load();
          Swal.fire({ icon: 'success', title: '已儲存', timer: 1200, showConfirmButton: false });
        } catch (e) {
          // 攔截器已顯示訊息
        } finally {
          this.saving = false;
        }
      },
      async resetPassword(u) {
        const result = await Swal.fire({
          title: `重設 ${u.displayName} 的密碼`,
          input: 'password',
          inputLabel: '新密碼（至少 8 個字元）',
          showCancelButton: true, confirmButtonText: '重設', cancelButtonText: '取消',
        });
        if (!result.isConfirmed || !result.value) return;
        await window.api.post(`/api/v1/users/${u.id}/reset-password`, { newPassword: result.value });
        Swal.fire({ icon: 'success', title: '已重設密碼', timer: 1400, showConfirmButton: false });
      },
      async toggleActive(u) {
        try {
          await window.api.put('/api/v1/users/' + u.id, {
            displayName: u.displayName, email: u.email, departmentId: u.departmentId,
            role: u.role, isActive: !u.isActive,
          });
          await this.load();
        } catch (e) {
          // 攔截器已顯示訊息（例如不能停用或降級自己的帳號）
        }
      },
    },
    template: `
<div>
  <div class="d-flex align-items-center mb-3">
    <h1 class="h5 mb-0">員工管理</h1>
    <button class="btn btn-primary btn-sm ms-auto" @click="openCreate">＋ 新增帳號</button>
  </div>

  <div class="card-oc p-0">
    <table class="table table-hover align-middle mb-0">
      <thead class="table-light">
        <tr><th>員工編號</th><th>姓名</th><th>Email</th><th>部門</th><th>角色</th>
            <th>狀態</th><th class="text-end">操作</th></tr>
      </thead>
      <tbody>
        <tr v-for="u in users" :key="u.id">
          <td>{{ u.employeeNo }}</td>
          <td class="fw-semibold">{{ u.displayName }}</td>
          <td class="small text-muted">{{ u.email }}</td>
          <td class="small">{{ u.departmentName || '—' }}</td>
          <td>
            <span class="badge" :class="u.role === 'Admin' ? 'text-bg-primary' : 'text-bg-light'">
              {{ u.role === 'Admin' ? '系統管理員' : '一般員工' }}
            </span>
          </td>
          <td>
            <span class="badge" :class="u.isActive ? 'text-bg-success' : 'text-bg-secondary'">
              {{ u.isActive ? '啟用中' : '已停用' }}
            </span>
          </td>
          <td class="text-end text-nowrap">
            <button class="btn btn-sm btn-outline-primary me-1" @click="openEdit(u)">編輯</button>
            <button class="btn btn-sm btn-outline-secondary me-1" @click="resetPassword(u)">
              重設密碼
            </button>
            <button class="btn btn-sm"
                    :class="u.isActive ? 'btn-outline-danger' : 'btn-outline-success'"
                    @click="toggleActive(u)">{{ u.isActive ? '停用' : '啟用' }}</button>
          </td>
        </tr>
      </tbody>
    </table>
  </div>

  <div class="modal fade" tabindex="-1" ref="dialog">
    <div class="modal-dialog">
      <div class="modal-content" v-if="editing">
        <div class="modal-header">
          <h5 class="modal-title">{{ editing.id ? '編輯帳號' : '新增帳號' }}</h5>
          <button type="button" class="btn-close" data-bs-dismiss="modal"></button>
        </div>
        <div class="modal-body">
          <div class="mb-3" v-if="!editing.id">
            <label class="form-label">員工編號</label>
            <input class="form-control" v-model.trim="editing.employeeNo" maxlength="20" />
          </div>
          <div class="mb-3">
            <label class="form-label">姓名</label>
            <input class="form-control" v-model.trim="editing.displayName" maxlength="50" />
          </div>
          <div class="mb-3">
            <label class="form-label">Email</label>
            <input type="email" class="form-control" v-model.trim="editing.email" maxlength="100" />
          </div>
          <div class="mb-3">
            <label class="form-label">部門</label>
            <select class="form-select" v-model="editing.departmentId">
              <option :value="null">未指定</option>
              <option v-for="d in departments" :key="d.id" :value="d.id">{{ d.name }}</option>
            </select>
          </div>
          <div class="mb-3">
            <label class="form-label">角色</label>
            <select class="form-select" v-model="editing.role">
              <option value="Employee">一般員工</option>
              <option value="Admin">系統管理員</option>
            </select>
          </div>
          <div class="mb-3" v-if="!editing.id">
            <label class="form-label">初始密碼（至少 8 個字元）</label>
            <input type="password" class="form-control" v-model="editing.password" />
          </div>
          <div class="form-check" v-if="editing.id">
            <input class="form-check-input" type="checkbox" id="user-active" v-model="editing.isActive" />
            <label class="form-check-label" for="user-active">啟用中</label>
          </div>
        </div>
        <div class="modal-footer">
          <button class="btn btn-outline-secondary" data-bs-dismiss="modal">取消</button>
          <button class="btn btn-primary" :disabled="saving" @click="save">儲存</button>
        </div>
      </div>
    </div>
  </div>
</div>`,
  };
})();
