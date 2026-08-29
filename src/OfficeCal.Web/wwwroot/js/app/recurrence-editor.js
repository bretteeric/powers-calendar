// 結構化重複設定器。對外的 modelValue 就是後端的 RecurrencePatternDto（null = 不重複）。
// 使用者永遠看不到 RRULE 字串——轉換一律由後端的 RruleFormatter 負責。
(function () {
  const WEEKDAYS = [
    { value: 'Sunday', label: '日' },
    { value: 'Monday', label: '一' },
    { value: 'Tuesday', label: '二' },
    { value: 'Wednesday', label: '三' },
    { value: 'Thursday', label: '四' },
    { value: 'Friday', label: '五' },
    { value: 'Saturday', label: '六' },
  ];

  window.RecurrenceEditor = {
    props: {
      modelValue: { type: Object, default: null },
      // 事件起始日，用來推導預設值（yyyy-MM-dd）
      startDate: { type: String, required: true },
    },
    emits: ['update:modelValue'],
    data() {
      return { weekdays: WEEKDAYS, freq: 'None', p: this.defaults() };
    },
    computed: {
      startAsDate() { return window.api.parseDate(this.startDate); },
      nthLabel() {
        const d = this.startAsDate;
        const nth = Math.floor((d.getDate() - 1) / 7) + 1;
        return ['第一個', '第二個', '第三個', '第四個'][nth - 1] || '第四個';
      },
      // 起始日在 29–31 號時，該星期在當月必然只有「最後一個」這個位置成立
      // （Day+7 必超出當月天數），後端 ValidateStartMatches 恆要求 bySetPosition=-1，
      // 沒有其他合法值可選，因此 UI 上鎖定不可取消勾選。
      mustUseLastPosition() {
        const d = this.startAsDate;
        const nth = Math.floor((d.getDate() - 1) / 7) + 1;
        return nth > 4;
      },
    },
    watch: {
      modelValue: {
        immediate: true,
        handler(v) {
          if (!v) { this.freq = 'None'; this.p = this.defaults(); return; }
          this.freq = v.frequency;
          this.p = Object.assign(this.defaults(), v);
        },
      },
      // 起始日改變時，重新推導與起始日繫結的欄位（後端會驗證兩者必須一致）
      startDate() { if (this.freq !== 'None') { this.syncToStart(); this.emit(); } },
    },
    methods: {
      defaults() {
        return {
          frequency: 'Weekly',
          interval: 1,
          byWeekDays: [],
          monthlyMode: 'DayOfMonth',
          byMonthDay: null,
          bySetPosition: null,
          byPositionWeekDay: null,
          byMonth: null,
          endMode: 'UntilDate',
          untilDate: null,
          count: null,
        };
      },
      syncToStart() {
        const d = this.startAsDate;
        const dow = WEEKDAYS[d.getDay()].value;
        const nth = Math.floor((d.getDate() - 1) / 7) + 1;

        // 補進起始日對應的星期，保留使用者已勾選的其他星期（不要整組覆蓋掉）。
        if (this.freq === 'Weekly' && !this.p.byWeekDays.includes(dow)) this.p.byWeekDays.push(dow);

        if (this.freq === 'Monthly') {
          this.p.byMonthDay = d.getDate();
          this.p.byPositionWeekDay = dow;
          this.p.bySetPosition = nth > 4 ? -1 : nth;
        }
        if (this.freq === 'Yearly') {
          this.p.byMonth = d.getMonth() + 1;
          this.p.byMonthDay = d.getDate();
        }
        if (!this.p.untilDate) {
          const until = new Date(d.getFullYear(), d.getMonth() + 3, d.getDate());
          this.p.untilDate = window.api.toLocalIso(until).slice(0, 10);
        }
      },
      onFreqChange() {
        if (this.freq === 'None') { this.$emit('update:modelValue', null); return; }
        this.p.frequency = this.freq;
        this.p.interval = this.p.interval || 1;
        this.syncToStart();
        this.emit();
      },
      // 每月的兩種模式互切時，只補齊「目標模式所需、但目前缺失或非法」的欄位，
      // 不要整段重算——否則會覆蓋使用者手動勾選的「每月最後一個」
      // （bySetPosition=-1 在 nth<=4 時本來就是合法值，不該被切走再切回時洗掉）。
      // 唯一硬約束：起始日 >= 29 號（nth>4）時 bySetPosition 必須是 -1，
      // 這條優先於「保留現值」。
      onMonthlyModeChange() {
        const d = this.startAsDate;
        const dow = WEEKDAYS[d.getDay()].value;
        const nth = Math.floor((d.getDate() - 1) / 7) + 1;

        if (this.p.monthlyMode === 'DayOfMonth') {
          const validDay = Number.isInteger(this.p.byMonthDay)
            && this.p.byMonthDay >= 1 && this.p.byMonthDay <= 31;
          if (!validDay) this.p.byMonthDay = d.getDate();
        } else {
          if (!this.p.byPositionWeekDay) this.p.byPositionWeekDay = dow;
          if (nth > 4) {
            this.p.bySetPosition = -1;
          } else if (![1, 2, 3, 4, -1].includes(this.p.bySetPosition)) {
            this.p.bySetPosition = nth;
          }
        }
        this.emit();
      },
      toggleWeekday(v) {
        const i = this.p.byWeekDays.indexOf(v);
        if (i >= 0) this.p.byWeekDays.splice(i, 1);
        else this.p.byWeekDays.push(v);
        this.emit();
      },
      emit() {
        if (this.freq === 'None') { this.$emit('update:modelValue', null); return; }
        const out = {
          frequency: this.freq,
          interval: Number(this.p.interval) || 1,
          byWeekDays: this.freq === 'Weekly' ? this.p.byWeekDays.slice() : [],
          monthlyMode: this.p.monthlyMode,
          byMonthDay: null,
          bySetPosition: null,
          byPositionWeekDay: null,
          byMonth: null,
          endMode: this.p.endMode,
          untilDate: this.p.endMode === 'UntilDate' ? this.p.untilDate : null,
          count: this.p.endMode === 'Count' ? (Number(this.p.count) || 1) : null,
        };
        if (this.freq === 'Monthly' && this.p.monthlyMode === 'DayOfMonth') {
          out.byMonthDay = Number(this.p.byMonthDay);
        }
        if (this.freq === 'Monthly' && this.p.monthlyMode === 'WeekDayOfMonth') {
          out.bySetPosition = Number(this.p.bySetPosition);
          out.byPositionWeekDay = this.p.byPositionWeekDay;
        }
        if (this.freq === 'Yearly') {
          out.byMonth = Number(this.p.byMonth);
          out.byMonthDay = Number(this.p.byMonthDay);
        }
        this.$emit('update:modelValue', out);
      },
    },
    template: `
<div class="border rounded p-3 bg-light-subtle">
  <div class="row g-2 align-items-end">
    <div class="col-sm-5">
      <label class="form-label small mb-1">重複</label>
      <select class="form-select form-select-sm" v-model="freq" @change="onFreqChange">
        <option value="None">不重複</option>
        <option value="Daily">每天</option>
        <option value="Weekly">每週</option>
        <option value="Monthly">每月</option>
        <option value="Yearly">每年</option>
      </select>
    </div>
    <div class="col-sm-4" v-if="freq !== 'None'">
      <label class="form-label small mb-1">間隔</label>
      <div class="input-group input-group-sm">
        <span class="input-group-text">每</span>
        <input type="number" min="1" max="999" class="form-control"
               v-model.number="p.interval" @change="emit" />
        <span class="input-group-text">
          {{ { Daily:'天', Weekly:'週', Monthly:'個月', Yearly:'年' }[freq] }}
        </span>
      </div>
    </div>
  </div>

  <div class="mt-3" v-if="freq === 'Weekly'">
    <label class="form-label small mb-1">星期（可複選）</label>
    <div class="btn-group d-flex flex-wrap" role="group">
      <button type="button" class="btn btn-sm me-1 mb-1"
              v-for="w in weekdays" :key="w.value"
              :class="p.byWeekDays.includes(w.value) ? 'btn-primary' : 'btn-outline-secondary'"
              @click="toggleWeekday(w.value)">{{ w.label }}</button>
    </div>
  </div>

  <div class="mt-3" v-if="freq === 'Monthly'">
    <div class="form-check">
      <input class="form-check-input" type="radio" id="mm-day" value="DayOfMonth"
             v-model="p.monthlyMode" @change="onMonthlyModeChange" />
      <label class="form-check-label small" for="mm-day">每月 {{ p.byMonthDay }} 日</label>
    </div>
    <div class="form-check">
      <input class="form-check-input" type="radio" id="mm-nth" value="WeekDayOfMonth"
             v-model="p.monthlyMode" @change="onMonthlyModeChange" />
      <label class="form-check-label small" for="mm-nth">
        每月{{ p.bySetPosition === -1 ? '最後一個' : nthLabel }}星期{{
          weekdays.find(w => w.value === p.byPositionWeekDay)
            ? weekdays.find(w => w.value === p.byPositionWeekDay).label : '' }}
      </label>
    </div>
    <div class="form-check ms-4" v-if="p.monthlyMode === 'WeekDayOfMonth'">
      <input class="form-check-input" type="checkbox" id="mm-last"
             :checked="p.bySetPosition === -1"
             :disabled="mustUseLastPosition"
             @change="p.bySetPosition = $event.target.checked ? -1
                        : Math.floor((startAsDate.getDate() - 1) / 7) + 1; emit()" />
      <label class="form-check-label small" for="mm-last">改用「每月最後一個」</label>
    </div>
  </div>

  <div class="mt-3" v-if="freq === 'Yearly'">
    <span class="small">每年 {{ p.byMonth }} 月 {{ p.byMonthDay }} 日</span>
  </div>

  <div class="mt-3" v-if="freq !== 'None'">
    <label class="form-label small mb-1">結束條件（必填）</label>
    <div class="row g-2">
      <div class="col-sm-6">
        <div class="input-group input-group-sm">
          <div class="input-group-text">
            <input class="form-check-input mt-0" type="radio" value="UntilDate"
                   v-model="p.endMode" @change="emit" />
          </div>
          <input type="date" class="form-control" v-model="p.untilDate"
                 :disabled="p.endMode !== 'UntilDate'" @change="emit" />
        </div>
      </div>
      <div class="col-sm-6">
        <div class="input-group input-group-sm">
          <div class="input-group-text">
            <input class="form-check-input mt-0" type="radio" value="Count"
                   v-model="p.endMode" @change="emit" />
          </div>
          <input type="number" min="1" max="730" class="form-control" placeholder="重複次數"
                 v-model.number="p.count" :disabled="p.endMode !== 'Count'" @change="emit" />
          <span class="input-group-text">次</span>
        </div>
      </div>
    </div>
    <div class="form-text">重複事件必須有結束日期或次數，且展開後不得超過 730 次。</div>
  </div>
</div>`,
  };
})();
