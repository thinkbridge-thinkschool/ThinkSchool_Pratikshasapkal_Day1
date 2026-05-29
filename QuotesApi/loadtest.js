import http from 'k6/http';

export const options = {
    vus: 20,
    duration: '30s',
    summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(90)', 'p(95)', 'p(99)'],
};

export default function () {
http.get('http://localhost:5032/fast-authors-with-quotes-projection');
}